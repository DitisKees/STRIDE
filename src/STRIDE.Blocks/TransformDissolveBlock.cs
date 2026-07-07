using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using STRIDE.Abstractions;
using System.Collections.Immutable;

namespace STRIDE.Blocks;

[StrideBlock("TransformDissolve")]
public sealed class TransformDissolveBlock(string? groupBy = null) : ITransformBlock
{
    private readonly string? _groupBy = groupBy;

    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (string.IsNullOrWhiteSpace(_groupBy))
        {
            return new Schema(ImmutableArray.Create(new FieldDef("geom", FieldType.Geometry, true)));
        }

        if (!input.TryGetOrdinal(_groupBy, out var ordinal))
        {
            throw new InvalidOperationException($"TransformDissolve group field '{_groupBy}' does not exist.");
        }

        return new Schema(ImmutableArray.Create(input.Fields[ordinal], new FieldDef("geom", FieldType.Geometry, true)));
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        var groups = new Dictionary<string, List<Geometry>>(StringComparer.Ordinal);
        Schema? inputSchema = null;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                inputSchema ??= batch.Schema;
                var geomOrdinal = batch.Schema.GeometryFieldIndex;
                if (geomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformDissolve requires a geometry field.");
                }

                var keyOrdinal = !string.IsNullOrWhiteSpace(_groupBy) && batch.Schema.TryGetOrdinal(_groupBy, out var resolved)
                    ? resolved
                    : -1;

                var geometries = batch.GeometryColumn(geomOrdinal).Values;
                for (var row = 0; row < batch.RowCount; row++)
                {
                    if (geometries[row] is not Geometry geometry)
                    {
                        continue;
                    }

                    var key = keyOrdinal >= 0 ? batch.GetValueAsString(keyOrdinal, row) : string.Empty;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = [];
                        groups[key] = list;
                    }

                    list.Add(geometry);
                }
            }
        }

        if (inputSchema is null || groups.Count == 0)
        {
            yield break;
        }

        var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = inputSchema });
        var rows = new string[groups.Count][];
        var dissolved = new Geometry?[groups.Count];

        var index = 0;
        foreach (var (key, value) in groups)
        {
            rows[index] = new string[outputSchema.Fields.Length];
            if (!string.IsNullOrWhiteSpace(_groupBy))
            {
                rows[index][0] = key;
            }

            dissolved[index] = UnaryUnionOp.Union(value);
            index++;
        }

        var batchOutput = RecordBatch.FromRows(outputSchema, rows);
        var columns = BatchTransformUtilities.CopyColumns(batchOutput);
        columns[outputSchema.GeometryFieldIndex] = new GeometryColumn(dissolved);
        yield return new RecordBatch(outputSchema, groups.Count, columns);
    }
}
