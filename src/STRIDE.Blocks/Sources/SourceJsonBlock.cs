using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace STRIDE.Blocks;

[StrideBlock("SourceJson")]
public sealed class SourceJsonBlock(string path, int batchSize = 1000) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private IReadOnlyList<Dictionary<string, string?>>? _rows;
    private Schema? _schema;

    public Schema DeriveOutputSchema()
    {
        EnsureLoaded();
        return _schema!;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureLoaded();

        var schema = _schema!;
        var rows = _rows!;

        for (var i = 0; i < rows.Count; i += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_batchSize, rows.Count - i);
            var batchRows = new string[count][];
            for (var r = 0; r < count; r++)
            {
                batchRows[r] = new string[schema.Fields.Length];
                for (var c = 0; c < schema.Fields.Length; c++)
                {
                    var name = schema.Fields[c].Name;
                    batchRows[r][c] = rows[i + r].TryGetValue(name, out var value) ? (value ?? string.Empty) : string.Empty;
                }
            }

            yield return RecordBatch.FromRows(schema, batchRows);
            await Task.Yield();
        }
    }

    private void EnsureLoaded()
    {
        if (_rows is not null && _schema is not null)
        {
            return;
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("SourceJson expects a top-level JSON array.");
        }

        var rows = new List<Dictionary<string, string?>>();
        var fields = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => property.Value.GetRawText(),
                };

                row[property.Name] = value;

                if (!fields.TryGetValue(property.Name, out var values))
                {
                    values = [];
                    fields[property.Name] = values;
                }

                values.Add(value);
            }

            rows.Add(row);
        }

        var outputFields = fields.Select(static item => new FieldDef(item.Key, InferFieldType(item.Value), true)).ToImmutableArray();

        _rows = rows;
        _schema = new Schema(outputFields);
    }

    private static FieldType InferFieldType(IReadOnlyList<string?> values)
    {
        var hasValue = false;
        var allInt = true;
        var allDouble = true;
        var allBool = true;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasValue = true;
            if (allInt && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                allInt = false;
            }

            if (allDouble && !double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
            {
                allDouble = false;
            }

            if (allBool && !bool.TryParse(value, out _))
            {
                allBool = false;
            }
        }

        return !hasValue
            ? FieldType.Utf8String
            : allInt
                ? FieldType.Int64
                : allDouble
                    ? FieldType.Float64
                    : allBool
                        ? FieldType.Boolean
                        : FieldType.Utf8String;
    }
}
