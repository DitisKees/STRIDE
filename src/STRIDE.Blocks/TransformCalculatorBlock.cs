using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace STRIDE.Blocks;

[StrideBlock("TransformCalculator")]
public sealed partial class TransformCalculatorBlock(string expression, string outputField) : ITransformBlock
{
    private readonly CalculationSpec _spec = CalculationSpec.Parse(expression);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.TryGetOrdinal(outputField, out var ordinal))
        {
            var fields = input.Fields.ToArray();
            fields[ordinal] = new FieldDef(outputField, FieldType.Float64, true);
            return new Schema(fields.ToImmutableArray());
        }

        return new Schema(input.Fields.Add(new FieldDef(outputField, FieldType.Float64, true)));
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
                if (!batch.Schema.TryGetOrdinal(_spec.Field, out var inputOrdinal))
                {
                    throw new InvalidOperationException($"TransformCalculator field '{_spec.Field}' does not exist.");
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

                    var value = batch.GetValueAsString(inputOrdinal, row);
                    if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var left))
                    {
                        rows[row][outputOrdinal] = string.Empty;
                        continue;
                    }

                    var computed = _spec.Operator switch
                    {
                        "+" => left + _spec.Constant,
                        "-" => left - _spec.Constant,
                        "*" => left * _spec.Constant,
                        "/" => _spec.Constant == 0 ? 0 : left / _spec.Constant,
                        _ => left,
                    };

                    rows[row][outputOrdinal] = computed.ToString(CultureInfo.InvariantCulture);
                }

                yield return RecordBatch.FromRows(outputSchema, rows);
            }
        }
    }

    private readonly record struct CalculationSpec(string Field, string Operator, double Constant)
    {
        public static CalculationSpec Parse(string expression)
        {
            var match = Pattern().Match(expression);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Unsupported calculator expression '{expression}'. Expected '<field> <op> <number>'.");
            }

            return new CalculationSpec(
                match.Groups["field"].Value,
                match.Groups["op"].Value,
                double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture));
        }
    }

    [GeneratedRegex("^(?<field>[A-Za-z_][A-Za-z0-9_]*)\\s*(?<op>[+\\-*/])\\s*(?<value>-?[0-9]+(?:\\.[0-9]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
