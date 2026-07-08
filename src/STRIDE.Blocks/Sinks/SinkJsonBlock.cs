using STRIDE.Abstractions;
using System.Text.Json;

namespace STRIDE.Blocks;

[StrideBlock("SinkJson")]
public sealed class SinkJsonBlock(string path, bool includeNullAndEmpty = false) : ISinkBlock
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

        var rowsWritten = 0L;
        var batchesWritten = 0;
        var completed = false;

        await using (var stream = File.Create(destinationPath))
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            var arrayClosed = false;

            writer.WriteStartArray();

            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var batch))
                    {
                        for (var row = 0; row < batch.RowCount; row++)
                        {
                            writer.WriteStartObject();
                            for (var col = 0; col < batch.Schema.Fields.Length; col++)
                            {
                                var field = batch.Schema.Fields[col];
                                var value = batch.GetValueAsString(col, row);
                                if (!includeNullAndEmpty && string.IsNullOrWhiteSpace(value))
                                {
                                    continue;
                                }

                                writer.WritePropertyName(field.Name);
                                switch (field.Type)
                                {
                                    case FieldType.Boolean:
                                        writer.WriteBooleanValue(batch.Column<bool>(col)[row]);
                                        break;
                                    case FieldType.Int32:
                                        writer.WriteNumberValue(batch.Column<int>(col)[row]);
                                        break;
                                    case FieldType.Int64:
                                        writer.WriteNumberValue(batch.Column<long>(col)[row]);
                                        break;
                                    case FieldType.Float64:
                                        writer.WriteNumberValue(batch.Column<double>(col)[row]);
                                        break;
                                    case FieldType.Null:
                                        writer.WriteNullValue();
                                        break;
                                    default:
                                        if (string.IsNullOrWhiteSpace(value))
                                        {
                                            writer.WriteNullValue();
                                        }
                                        else
                                        {
                                            writer.WriteStringValue(value);
                                        }

                                        break;
                                }
                            }

                            writer.WriteEndObject();
                            rowsWritten++;
                        }

                        batchesWritten++;
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                writer.WriteEndArray();
                arrayClosed = true;
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            catch
            {
                if (!arrayClosed && !writeMode.IsTransactional)
                {
                    TryCompleteJsonArray(writer);
                }

                if (writeMode.IsTransactional)
                {
                    TryDelete(destinationPath);
                }
                else
                {
                    await context.Errors.WriteRecordErrorAsync(
                        context.NodeId,
                        $"SinkJson partial write retained at '{path}'. rowsWritten={rowsWritten}, batchesWritten={batchesWritten}.",
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

    private static void TryCompleteJsonArray(Utf8JsonWriter writer)
    {
        try
        {
            writer.WriteEndArray();
            writer.Flush();
        }
        catch
        {
            // Best-effort close for partial files.
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
