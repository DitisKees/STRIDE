using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using STRIDE.Abstractions;
using System.Runtime.InteropServices;

namespace STRIDE.Blocks;

[StrideBlock("TransformSpatialJoin")]
public sealed class TransformSpatialJoinBlock(string predicate = "Intersects") : ITransformBlock
{
    private readonly string _predicate = predicate;

    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformSpatialJoin requires a geometry field on the 'in' input.");
        }

        if (!inputSchemas.TryGetValue("lookup", out var lookup) || lookup.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformSpatialJoin requires a geometry field on the 'lookup' input.");
        }

        return input;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("lookup", out var lookupReader) || !context.Inputs.TryGetValue("in", out var inputReader))
        {
            yield break;
        }

        var index = new STRtree<Geometry>();

        while (await lookupReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (lookupReader.TryRead(out var lookupBatch))
            {
                var lookupGeomOrdinal = lookupBatch.Schema.GeometryFieldIndex;
                if (lookupGeomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformSpatialJoin requires geometry in the lookup stream.");
                }

                var values = lookupBatch.GeometryColumn(lookupGeomOrdinal).Values;
                for (var row = 0; row < lookupBatch.RowCount; row++)
                {
                    if (values[row] is Geometry geometry)
                    {
                        index.Insert(geometry.EnvelopeInternal, geometry);
                    }
                }
            }
        }

        index.Build();

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
                    throw new InvalidOperationException("TransformSpatialJoin requires geometry in the input stream.");
                }

                var inputGeometries = inputBatch.GeometryColumn(inputGeomOrdinal).Values;
                var selectedRows = new List<int>(inputBatch.RowCount);

                for (var row = 0; row < inputBatch.RowCount; row++)
                {
                    if (inputGeometries[row] is not Geometry geometry)
                    {
                        continue;
                    }

                    var candidates = index.Query(geometry.EnvelopeInternal);
                    var matched = candidates.Any(candidate => Matches(geometry, candidate));
                    if (matched)
                    {
                        selectedRows.Add(row);
                    }
                }

                if (selectedRows.Count == 0)
                {
                    continue;
                }

                if (inputBatch is RecordBatch recordBatch)
                {
                    yield return recordBatch.SelectRows(CollectionsMarshal.AsSpan(selectedRows));
                }
                else
                {
                    var rows = new string[selectedRows.Count][];
                    for (var i = 0; i < selectedRows.Count; i++)
                    {
                        rows[i] = new string[inputBatch.Schema.Fields.Length];
                        var sourceRow = selectedRows[i];
                        for (var c = 0; c < inputBatch.Schema.Fields.Length; c++)
                        {
                            rows[i][c] = inputBatch.GetValueAsString(c, sourceRow);
                        }
                    }

                    yield return RecordBatch.FromRows(inputBatch.Schema, rows);
                }
            }
        }
    }

    private bool Matches(Geometry left, Geometry right)
        => _predicate.ToLowerInvariant() switch
        {
            "contains" => left.Contains(right),
            "within" => left.Within(right),
            _ => left.Intersects(right),
        };
}
