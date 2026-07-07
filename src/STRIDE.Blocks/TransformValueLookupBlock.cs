using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformValueLookup")]
public sealed class TransformValueLookupBlock(string field, string mappings, string? outputField = null) : ITransformBlock
{
    private readonly IReadOnlyDictionary<string, string> _map = ParseMappings(mappings);
    private readonly string _outputField = outputField ?? field;

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.TryGetOrdinal(_outputField, out _))
        {
            return input;
        }

        return new Schema(input.Fields.Add(new FieldDef(_outputField, FieldType.Utf8String, true)));
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
                    throw new InvalidOperationException($"TransformValueLookup field '{field}' does not exist.");
                }

                var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = batch.Schema });
                _ = outputSchema.TryGetOrdinal(_outputField, out var outputOrdinal);

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
                    rows[row][outputOrdinal] = _map.TryGetValue(sourceValue, out var mapped) ? mapped : sourceValue;
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMappings(string mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappings.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (split.Length != 2)
            {
                throw new InvalidOperationException($"Invalid mapping '{pair}'. Expected 'source=target'.");
            }

            result[split[0]] = split[1];
        }

        return result;
    }
}
