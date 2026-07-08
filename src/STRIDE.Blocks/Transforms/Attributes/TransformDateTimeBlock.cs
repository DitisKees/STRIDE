using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("TransformDateTime")]
public sealed class TransformDateTimeBlock(string field, string outputField = "datetime_utc") : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (!input.TryGetOrdinal(outputField, out var ordinal))
        {
            return new Schema(input.Fields.Add(new FieldDef(outputField, FieldType.DateTimeUtc, true)));
        }

        var fields = input.Fields.ToArray();
        fields[ordinal] = new FieldDef(outputField, FieldType.DateTimeUtc, true);
        return new Schema(fields.ToImmutableArray());
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
                if (!batch.Schema.TryGetOrdinal(field, out var sourceOrdinal))
                {
                    throw new InvalidOperationException($"TransformDateTime field '{field}' does not exist.");
                }

                var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = batch.Schema });
                _ = outputSchema.TryGetOrdinal(outputField, out var outputOrdinal);

                var rows = new string[batch.RowCount][];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    rows[row] = new string[outputSchema.Fields.Length];
                    for (var col = 0; col < outputSchema.Fields.Length; col++)
                    {
                        if (col < batch.Schema.Fields.Length)
                        {
                            rows[row][col] = batch.GetValueAsString(col, row);
                        }
                    }

                    var sourceValue = batch.GetValueAsString(sourceOrdinal, row);
                    if (DateTime.TryParse(sourceValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        rows[row][outputOrdinal] = parsed.ToString("O", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        rows[row][outputOrdinal] = string.Empty;
                    }
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }
}
