using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("TransformSpatialEnrich")]
public sealed class TransformSpatialEnrichBlock(
    string attributes = "*",
    string joinType = "left",
    string predicate = "Intersects") : ITransformBlock
{
    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        if (!inputSchemas.TryGetValue("in", out var inputSchema) || inputSchema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformSpatialEnrich requires a geometry field on the 'in' input.");
        }

        if (!inputSchemas.TryGetValue("lookup", out var lookupSchema) || lookupSchema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformSpatialEnrich requires a geometry field on the 'lookup' input.");
        }

        var lookupFields = ResolveLookupFields(lookupSchema).ToArray();

        var inputFieldNames = new HashSet<string>(
            inputSchema.Fields.Select(static f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        var conflicts = lookupFields
            .Where(f => inputFieldNames.Contains(f.Name))
            .Select(static f => f.Name)
            .ToArray();

        if (conflicts.Length > 0)
        {
            throw new InvalidOperationException(
                $"TransformSpatialEnrich: lookup attribute(s) conflict with existing input fields: {string.Join(", ", conflicts)}. " +
                "Rename the columns in the source query or use a prefix.");
        }

        var combined = inputSchema.Fields.AddRange(lookupFields.Select(static f => f with { Nullable = true }));
        return new Schema(combined);
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("lookup", out var lookupReader)
            || !context.Inputs.TryGetValue("in", out var inputReader))
        {
            yield break;
        }

        Schema? outputSchema = null;
        FieldDef[]? resolvedLookupFields = null;
        int[]? lookupOrdinals = null;

        // Phase 1: consume entire lookup stream into spatial index.
        var index = new STRtree<EnrichEntry>();

        while (await lookupReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (lookupReader.TryRead(out var lookupBatch))
            {
                if (lookupBatch.RowCount == 0)
                {
                    continue;
                }

                var lookupGeomOrdinal = lookupBatch.Schema.GeometryFieldIndex;
                if (lookupGeomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformSpatialEnrich requires geometry in the lookup stream.");
                }

                if (lookupOrdinals is null)
                {
                    resolvedLookupFields = ResolveLookupFields(lookupBatch.Schema).ToArray();
                    lookupOrdinals = resolvedLookupFields
                        .Select(f => lookupBatch.Schema.TryGetOrdinal(f.Name, out var ord) ? ord : -1)
                        .ToArray();
                }

                var geomColumn = lookupBatch.GeometryColumn(lookupGeomOrdinal).Values;

                for (var row = 0; row < lookupBatch.RowCount; row++)
                {
                    if (geomColumn[row] is not Geometry geom)
                    {
                        continue;
                    }

                    var values = new string?[lookupOrdinals.Length];
                    for (var i = 0; i < lookupOrdinals.Length; i++)
                    {
                        var ord = lookupOrdinals[i];
                        values[i] = ord >= 0 ? lookupBatch.GetValueAsString(ord, row) : null;
                    }

                    index.Insert(geom.EnvelopeInternal, new EnrichEntry(geom, values));
                }
            }
        }

        index.Build();
        resolvedLookupFields ??= [];
        lookupOrdinals ??= [];

        var isLeft = !string.Equals(joinType, "inner", StringComparison.OrdinalIgnoreCase);
        var nullValues = new string?[lookupOrdinals.Length];
        var maxDop = BatchTransformUtilities.ResolveMaxDegreeOfParallelism(context.Parameters);

        // Phase 2: stream input batches and enrich.
        while (await inputReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (inputReader.TryRead(out var inputBatch))
            {
                if (inputBatch.RowCount == 0)
                {
                    continue;
                }

                var inputGeomOrdinal = inputBatch.Schema.GeometryFieldIndex;
                if (inputGeomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformSpatialEnrich requires geometry in the input stream.");
                }

                outputSchema ??= BuildOutputSchema(inputBatch.Schema, resolvedLookupFields);

                var inputGeoms = inputBatch.GeometryColumn(inputGeomOrdinal).Values;

                // Per-row: index into input batch + matched lookup values (null = skip for inner join).
                var matchedValues = new string?[]?[inputBatch.RowCount];

                if (maxDop <= 1)
                {
                    for (var row = 0; row < inputBatch.RowCount; row++)
                    {
                        matchedValues[row] = FindMatch(index, inputGeoms[row]) ?? (isLeft ? nullValues : null);
                    }
                }
                else
                {
                    Parallel.ForEach(
                        Partitioner.Create(0, inputBatch.RowCount),
                        new ParallelOptions
                        {
                            CancellationToken = cancellationToken,
                            MaxDegreeOfParallelism = maxDop,
                        },
                        range =>
                        {
                            for (var row = range.Item1; row < range.Item2; row++)
                            {
                                matchedValues[row] = FindMatch(index, inputGeoms[row]) ?? (isLeft ? nullValues : null);
                            }
                        });
                }

                // Collect which input rows pass (all for left, matched-only for inner).
                var selectedRows = new List<int>(inputBatch.RowCount);
                for (var row = 0; row < inputBatch.RowCount; row++)
                {
                    if (matchedValues[row] is not null)
                    {
                        selectedRows.Add(row);
                    }
                }

                if (selectedRows.Count == 0)
                {
                    continue;
                }

                yield return BuildEnrichedBatch(
                    inputBatch,
                    outputSchema,
                    selectedRows,
                    matchedValues,
                    resolvedLookupFields);
            }
        }
    }

    private IEnumerable<FieldDef> ResolveLookupFields(Schema lookupSchema)
    {
        if (string.Equals(attributes, "*", StringComparison.Ordinal))
        {
            return lookupSchema.Fields.Where(static f => f.Type != FieldType.Geometry);
        }

        return attributes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name =>
            {
                if (!lookupSchema.TryGetOrdinal(name, out var ord))
                {
                    throw new InvalidOperationException(
                        $"TransformSpatialEnrich: attribute '{name}' not found in lookup schema.");
                }

                return lookupSchema.Fields[ord];
            });
    }

    private static Schema BuildOutputSchema(Schema inputSchema, FieldDef[] lookupFields)
    {
        var combined = inputSchema.Fields.AddRange(lookupFields.Select(static f => f with { Nullable = true }));
        return new Schema(combined);
    }

    private string?[]? FindMatch(STRtree<EnrichEntry> index, Geometry? geometry)
    {
        if (geometry is null)
        {
            return null;
        }

        var candidates = index.Query(geometry.EnvelopeInternal);
        foreach (var candidate in candidates)
        {
            if (Matches(geometry, candidate.Geometry))
            {
                return candidate.Values;
            }
        }

        return null;
    }

    private static RecordBatch BuildEnrichedBatch(
        IRecordBatch inputBatch,
        Schema outputSchema,
        List<int> selectedRows,
        string?[]?[] matchedValues,
        FieldDef[] lookupFields)
    {
        var lookupFieldCount = lookupFields.Length;
        var inputFieldCount = outputSchema.Fields.Length - lookupFieldCount;
        var rowCount = selectedRows.Count;
        var columns = new object?[outputSchema.Fields.Length];

        // Copy input columns, selecting only the chosen rows.
        for (var c = 0; c < inputFieldCount; c++)
        {
            columns[c] = ExtractInputColumn(inputBatch, c, selectedRows);
        }

        // Build lookup columns from stored string values.
        for (var i = 0; i < lookupFieldCount; i++)
        {
            var col = inputFieldCount + i;
            var stringValues = new string?[rowCount];
            for (var r = 0; r < rowCount; r++)
            {
                stringValues[r] = matchedValues[selectedRows[r]]?[i];
            }

            columns[col] = BuildLookupColumn(lookupFields[i].Type, stringValues, rowCount);
        }

        return new RecordBatch(outputSchema, rowCount, columns);
    }

    private static object? ExtractInputColumn(IRecordBatch batch, int ordinal, List<int> rows)
    {
        var rowCount = rows.Count;
        return batch.Schema.Fields[ordinal].Type switch
        {
            FieldType.Int32 => SelectPrimitive<int>(batch.Column<int>(ordinal), rows, rowCount),
            FieldType.Int64 => SelectPrimitive<long>(batch.Column<long>(ordinal), rows, rowCount),
            FieldType.Float64 => SelectPrimitive<double>(batch.Column<double>(ordinal), rows, rowCount),
            FieldType.Boolean => SelectPrimitive<bool>(batch.Column<bool>(ordinal), rows, rowCount),
            FieldType.Geometry => SelectGeometry(batch.GeometryColumn(ordinal).Values, rows, rowCount),
            _ => SelectStrings(batch, ordinal, rows, rowCount),
        };
    }

    private static T[] SelectPrimitive<T>(ReadOnlySpan<T> source, List<int> rows, int rowCount)
        where T : unmanaged
    {
        var target = new T[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            target[i] = source[rows[i]];
        }

        return target;
    }

    private static GeometryColumn SelectGeometry(IReadOnlyList<Geometry?> source, List<int> rows, int rowCount)
    {
        var target = new Geometry?[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            target[i] = source[rows[i]];
        }

        return new GeometryColumn(target);
    }

    private static Utf8StringColumn SelectStrings(IRecordBatch batch, int ordinal, List<int> rows, int rowCount)
    {
        var values = new string?[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            values[i] = batch.GetValueAsString(ordinal, rows[i]);
        }

        return RecordBatch.CreateUtf8Column(values);
    }

    private static object? BuildLookupColumn(FieldType type, string?[] values, int rowCount)
    {
        switch (type)
        {
            case FieldType.Int32:
                {
                    var typed = new int[rowCount];
                    for (var r = 0; r < rowCount; r++)
                    {
                        if (!string.IsNullOrWhiteSpace(values[r])
                            && int.TryParse(values[r], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            typed[r] = parsed;
                        }
                    }

                    return typed;
                }

            case FieldType.Int64:
                {
                    var typed = new long[rowCount];
                    for (var r = 0; r < rowCount; r++)
                    {
                        if (!string.IsNullOrWhiteSpace(values[r])
                            && long.TryParse(values[r], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            typed[r] = parsed;
                        }
                    }

                    return typed;
                }

            case FieldType.Float64:
                {
                    var typed = new double[rowCount];
                    for (var r = 0; r < rowCount; r++)
                    {
                        if (!string.IsNullOrWhiteSpace(values[r])
                            && double.TryParse(values[r], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                        {
                            typed[r] = parsed;
                        }
                    }

                    return typed;
                }

            case FieldType.Boolean:
                {
                    var typed = new bool[rowCount];
                    for (var r = 0; r < rowCount; r++)
                    {
                        if (!string.IsNullOrWhiteSpace(values[r])
                            && bool.TryParse(values[r], out var parsed))
                        {
                            typed[r] = parsed;
                        }
                    }

                    return typed;
                }

            default:
                return RecordBatch.CreateUtf8Column(values);
        }
    }

    private bool Matches(Geometry left, Geometry right)
        => predicate.ToLowerInvariant() switch
        {
            "contains" => left.Contains(right),
            "within" => left.Within(right),
            _ => left.Intersects(right),
        };

    private sealed record EnrichEntry(Geometry Geometry, string?[] Values);
}