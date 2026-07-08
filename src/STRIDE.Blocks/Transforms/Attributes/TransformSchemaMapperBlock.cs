using STRIDE.Abstractions;
using System.Collections.Immutable;

namespace STRIDE.Blocks;

[StrideBlock("TransformSchemaMapper")]
public sealed class TransformSchemaMapperBlock(string fields, string? rename = null, char delimiter = ',') : ITransformBlock
{
    private readonly string[] _fields = fields.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private readonly IReadOnlyDictionary<string, string> _renameMap = ParseRename(rename);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        var outputFields = new List<FieldDef>(_fields.Length);

        foreach (var fieldName in _fields)
        {
            if (!input.TryGetOrdinal(fieldName, out var ordinal))
            {
                throw new InvalidOperationException($"TransformSchemaMapper field '{fieldName}' does not exist.");
            }

            var sourceField = input.Fields[ordinal];
            outputFields.Add(sourceField with
            {
                Name = _renameMap.TryGetValue(fieldName, out var renamed) ? renamed : sourceField.Name,
            });
        }

        return new Schema(outputFields.ToImmutableArray());
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
                var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = batch.Schema });
                var rows = new string[batch.RowCount][];

                for (var row = 0; row < batch.RowCount; row++)
                {
                    rows[row] = new string[_fields.Length];
                    for (var col = 0; col < _fields.Length; col++)
                    {
                        var sourceName = _fields[col];
                        _ = batch.Schema.TryGetOrdinal(sourceName, out var sourceOrdinal);
                        rows[row][col] = batch.GetValueAsString(sourceOrdinal, row);
                    }
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseRename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (split.Length != 2)
            {
                throw new InvalidOperationException($"Invalid rename mapping '{part}'. Expected 'old:new'.");
            }

            map[split[0]] = split[1];
        }

        return map;
    }
}
