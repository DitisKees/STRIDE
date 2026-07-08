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

        var writeMode = SinkWriteModeUtilities.Parse(context.Parameters);
        var destinationPath = writeMode.IsTransactional
            ? SinkWriteModeUtilities.CreateTransactionalStagingPath(path)
            : path;

        var batchesWritten = 0;
        var rowsWritten = 0L;
        var completed = false;

        await using (var stream = File.Create(destinationPath))
        await using (var writer = new StreamWriter(stream))
        {
            try
            {
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
                            rowsWritten++;
                        }

                        batchesWritten++;
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            catch
            {
                if (writeMode.IsTransactional)
                {
                    TryDelete(destinationPath);
                }
                else
                {
                    await context.Errors.WriteRecordErrorAsync(
                        context.NodeId,
                        $"SinkCsv partial write retained at '{path}'. rowsWritten={rowsWritten}, batchesWritten={batchesWritten}.",
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }

        if (completed && writeMode.IsTransactional)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(destinationPath, path);
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
