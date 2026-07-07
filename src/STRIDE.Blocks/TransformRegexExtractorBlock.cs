using STRIDE.Abstractions;
using System.Text.RegularExpressions;

namespace STRIDE.Blocks;

[StrideBlock("TransformRegexExtractor")]
public sealed class TransformRegexExtractorBlock(string field, string pattern, string outputField, int groupIndex = 1) : ITransformBlock
{
    private readonly Regex _regex = new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.TryGetOrdinal(outputField, out _))
        {
            return input;
        }

        return new Schema(input.Fields.Add(new FieldDef(outputField, FieldType.Utf8String, true)));
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
                    throw new InvalidOperationException($"TransformRegexExtractor field '{field}' does not exist.");
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

                    var value = batch.GetValueAsString(sourceOrdinal, row);
                    var match = _regex.Match(value);
                    rows[row][outputOrdinal] = match.Success && match.Groups.Count > groupIndex ? match.Groups[groupIndex].Value : string.Empty;
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }
}
