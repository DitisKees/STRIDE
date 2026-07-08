using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformDeduplicateRecords")]
public sealed class TransformDeduplicateRecordsBlock(string? keyFields = null, char delimiter = ',') : ITransformBlock
{
    private readonly string[]? _keyFields = string.IsNullOrWhiteSpace(keyFields)
        ? null
        : keyFields.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selectedRows = new List<string[]>();
        Schema? schema = null;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                schema ??= batch.Schema;
                var keyOrdinals = ResolveKeyOrdinals(batch.Schema);

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var key = BuildKey(batch, row, keyOrdinals);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    var values = new string[batch.Schema.Fields.Length];
                    for (var col = 0; col < batch.Schema.Fields.Length; col++)
                    {
                        values[col] = batch.GetValueAsString(col, row);
                    }

                    selectedRows.Add(values);
                }
            }
        }

        if (schema is null || selectedRows.Count == 0)
        {
            yield break;
        }

        var batchSize = context.Parameters.GetOptionalInt32("batchSize") ?? 1000;
        for (var i = 0; i < selectedRows.Count; i += batchSize)
        {
            var count = Math.Min(batchSize, selectedRows.Count - i);
            var rows = new string[count][];
            for (var row = 0; row < count; row++)
            {
                rows[row] = selectedRows[i + row];
            }

            yield return RecordBatch.FromRows(schema, rows);
        }
    }

    private int[] ResolveKeyOrdinals(Schema schema)
    {
        if (_keyFields is null || _keyFields.Length == 0)
        {
            return Enumerable.Range(0, schema.Fields.Length).ToArray();
        }

        var ordinals = new int[_keyFields.Length];
        for (var i = 0; i < _keyFields.Length; i++)
        {
            if (!schema.TryGetOrdinal(_keyFields[i], out var ordinal))
            {
                throw new InvalidOperationException($"TransformDeduplicateRecords key field '{_keyFields[i]}' does not exist.");
            }

            ordinals[i] = ordinal;
        }

        return ordinals;
    }

    private static string BuildKey(IRecordBatch batch, int row, int[] keyOrdinals)
    {
        var parts = new string[keyOrdinals.Length];
        for (var i = 0; i < keyOrdinals.Length; i++)
        {
            parts[i] = batch.GetValueAsString(keyOrdinals[i], row);
        }

        return string.Join('\u001f', parts);
    }
}
