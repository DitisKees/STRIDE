using STRIDE.Abstractions;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("TransformStringManipulator")]
public sealed class TransformStringManipulatorBlock(string field, string operation, string? argument = null) : ITransformBlock
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
                    throw new InvalidOperationException($"TransformStringManipulator field '{field}' does not exist.");
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
                    values[row] = ApplyOperation(source.GetString(row));
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[ordinal] = RecordBatch.CreateUtf8Column(values);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }

    private string ApplyOperation(string value)
        => operation.ToLowerInvariant() switch
        {
            "trim" => value.Trim(),
            "upper" => value.ToUpper(CultureInfo.InvariantCulture),
            "lower" => value.ToLower(CultureInfo.InvariantCulture),
            "prefix" => (argument ?? string.Empty) + value,
            "suffix" => value + (argument ?? string.Empty),
            _ => value,
        };
}
