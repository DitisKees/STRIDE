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

        await using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartArray();

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
                }
            }
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
