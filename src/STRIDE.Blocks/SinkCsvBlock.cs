using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("SinkCsv")]
public sealed class SinkCsvBlock(string path, bool includeHeader = true, char delimiter = ',') : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);

        var headerWritten = false;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (!headerWritten && includeHeader)
                {
                    await writer.WriteLineAsync(string.Join(delimiter, batch.Schema.Fields.Select(static f => f.Name))).ConfigureAwait(false);
                    headerWritten = true;
                }

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var values = new string[batch.Schema.Fields.Length];
                    for (var column = 0; column < batch.Schema.Fields.Length; column++)
                    {
                        values[column] = batch.GetValueAsString(column, row);
                    }

                    await writer.WriteLineAsync(string.Join(delimiter, values)).ConfigureAwait(false);
                }
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
