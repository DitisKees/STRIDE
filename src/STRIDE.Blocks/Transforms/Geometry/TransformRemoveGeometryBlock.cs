using STRIDE.Abstractions;
using System.Collections.Immutable;

namespace STRIDE.Blocks;

[StrideBlock("TransformRemoveGeometry")]
public sealed class TransformRemoveGeometryBlock : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.GeometryFieldIndex < 0)
        {
            return input;
        }

        var fields = input.Fields
            .Where((field, index) => index != input.GeometryFieldIndex)
            .ToImmutableArray();
        return new Schema(fields);
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (batch.Schema.GeometryFieldIndex < 0)
                {
                    yield return BatchTransformUtilities.EnsureRecordBatch(batch);
                    continue;
                }

                var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = batch.Schema });
                var rows = new string[batch.RowCount][];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    rows[row] = new string[outputSchema.Fields.Length];
                    var outputColumn = 0;
                    for (var inputColumn = 0; inputColumn < batch.Schema.Fields.Length; inputColumn++)
                    {
                        if (inputColumn == batch.Schema.GeometryFieldIndex)
                        {
                            continue;
                        }

                        rows[row][outputColumn++] = batch.GetValueAsString(inputColumn, row);
                    }
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }
}
