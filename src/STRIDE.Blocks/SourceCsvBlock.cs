using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("SourceCsv")]
public sealed class SourceCsvBlock(
    string path,
    string? columns = null,
    bool hasHeader = true,
    char delimiter = ',',
    bool inferTypes = true,
    int inferenceSampleRows = 256) : ISourceBlock
{
    private readonly string[]? _configuredColumns = string.IsNullOrWhiteSpace(columns)
            ? null
            : columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private readonly int _inferenceSampleRows = Math.Max(1, inferenceSampleRows);
    private Schema? _cachedSchema;

    public Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        var lines = File.ReadLines(path).ToArray();
        if (lines.Length == 0)
        {
            var fallbackFields = ImmutableArray.Create(new FieldDef("value", FieldType.Utf8String, true));
            _cachedSchema = new Schema(fallbackFields);
            return _cachedSchema;
        }

        var dataStartIndex = 0;
        string[] columnNames;
        if (_configuredColumns is { Length: > 0 })
        {
            columnNames = _configuredColumns;
            if (hasHeader)
            {
                dataStartIndex = 1;
            }
        }
        else if (hasHeader)
        {
            columnNames = SplitCsv(lines[0]);
            dataStartIndex = 1;
        }
        else
        {
            var firstData = SplitCsv(lines[0]);
            columnNames = Enumerable.Range(1, firstData.Length).Select(static i => $"col{i}").ToArray();
        }

        var sampleRows = lines
            .Skip(dataStartIndex)
            .Take(_inferenceSampleRows)
            .Select(SplitCsv)
            .ToArray();

        var fields = new FieldDef[columnNames.Length];
        for (var i = 0; i < columnNames.Length; i++)
        {
            var type = inferTypes
                ? InferFieldType(sampleRows, i)
                : FieldType.Utf8String;
            fields[i] = new FieldDef(columnNames[i], type, true);
        }

        _cachedSchema = new Schema(fields.ToImmutableArray());
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();
        var batchSize = context.Parameters.GetOptionalInt32("batchSize") ?? 1000;
        var checkpointPath = context.Parameters.GetOptionalString("checkpointPath");
        var checkpointEvery = context.Parameters.GetOptionalInt32("checkpointEvery") ?? 100000;
        var resumeOffset = await ReadCheckpointAsync(checkpointPath, cancellationToken).ConfigureAwait(false);

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        if (hasHeader)
        {
            _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        var skipped = 0;
        while (skipped < resumeOffset)
        {
            var skippedLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (skippedLine is null)
            {
                break;
            }

            skipped++;
        }

        var processed = skipped;
        var rows = new List<string[]>(batchSize);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var values = SplitCsv(line);
            rows.Add(values);
            processed++;

            if (!string.IsNullOrWhiteSpace(checkpointPath)
                && checkpointEvery > 0
                && processed % checkpointEvery == 0)
            {
                await WriteCheckpointAsync(checkpointPath, processed, cancellationToken).ConfigureAwait(false);
            }

            if (rows.Count >= batchSize)
            {
                var batchRows = rows.ToArray();
                rows.Clear();
                yield return RecordBatch.FromRows(schema, batchRows);
            }
        }

        if (rows.Count > 0)
        {
            yield return RecordBatch.FromRows(schema, rows.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(checkpointPath))
        {
            await WriteCheckpointAsync(checkpointPath, processed, cancellationToken).ConfigureAwait(false);
        }
    }

    private string[] SplitCsv(string line)
        => line.Split(delimiter);

    private static FieldType InferFieldType(IReadOnlyList<string[]> rows, int columnIndex)
    {
        var hasValue = false;
        var allInt64 = true;
        var allFloat64 = true;
        var allBoolean = true;

        foreach (var row in rows)
        {
            var value = columnIndex < row.Length ? row[columnIndex] : string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasValue = true;

            if (allInt64 && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                allInt64 = false;
            }

            if (allFloat64 && !double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
            {
                allFloat64 = false;
            }

            if (allBoolean && !bool.TryParse(value, out _))
            {
                allBoolean = false;
            }
        }

        if (!hasValue)
        {
            return FieldType.Utf8String;
        }

        if (allInt64)
        {
            return FieldType.Int64;
        }

        if (allFloat64)
        {
            return FieldType.Float64;
        }

        if (allBoolean)
        {
            return FieldType.Boolean;
        }

        return FieldType.Utf8String;
    }

    private static async Task<int> ReadCheckpointAsync(string? checkpointPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkpointPath) || !File.Exists(checkpointPath))
        {
            return 0;
        }

        var text = await File.ReadAllTextAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static async Task WriteCheckpointAsync(string checkpointPath, int processed, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(checkpointPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(checkpointPath, processed.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }
}
