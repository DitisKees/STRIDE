using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformNullHandler")]
public sealed class TransformNullHandlerBlock(string field, string defaultValue) : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

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
                if (!batch.Schema.TryGetOrdinal(field, out var ordinal))
                {
                    throw new InvalidOperationException($"TransformNullHandler field '{field}' does not exist.");
                }

                if (batch.Schema.Fields[ordinal].Type != FieldType.Utf8String)
                {
                    yield return BatchTransformUtilities.EnsureRecordBatch(batch);
                    continue;
                }

                var source = batch.StringColumn(ordinal);
                var values = new string?[batch.RowCount];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    var value = source.GetString(row);
                    values[row] = string.IsNullOrWhiteSpace(value) ? defaultValue : value;
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[ordinal] = RecordBatch.CreateUtf8Column(values);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
