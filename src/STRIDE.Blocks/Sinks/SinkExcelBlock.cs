using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("SinkExcel")]
public sealed class SinkExcelBlock(string path) : ISinkBlock
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

        try
        {
            await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var document = SpreadsheetDocument.Create(fileStream, SpreadsheetDocumentType.Workbook, autoSave: true))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();

                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                using var worksheetWriter = OpenXmlWriter.Create(worksheetPart);

                worksheetWriter.WriteStartElement(new Worksheet());
                worksheetWriter.WriteStartElement(new SheetData());

                var rowIndex = 1u;
                var headerWritten = false;

                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var batch))
                    {
                        if (!headerWritten)
                        {
                            WriteRow(
                                worksheetWriter,
                                rowIndex++,
                                batch.Schema.Fields.Select(static field => field.Name).ToArray());
                            headerWritten = true;
                        }

                        for (var row = 0; row < batch.RowCount; row++)
                        {
                            var values = new string?[batch.Schema.Fields.Length];
                            for (var col = 0; col < batch.Schema.Fields.Length; col++)
                            {
                                values[col] = batch.GetValueAsString(col, row);
                            }

                            WriteRow(worksheetWriter, rowIndex++, values);
                            rowsWritten++;
                        }

                        batchesWritten++;
                        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                worksheetWriter.WriteEndElement();
                worksheetWriter.WriteEndElement();
                worksheetWriter.Close();

                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "Sheet1",
                });

                workbookPart.Workbook.Save();
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                completed = true;
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
                    $"SinkExcel partial write retained at '{path}'. rowsWritten={rowsWritten}, batchesWritten={batchesWritten}.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, IReadOnlyList<string?> values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });

        for (var col = 0; col < values.Count; col++)
        {
            var value = values[col];
            writer.WriteStartElement(new Cell { DataType = CellValues.InlineString });
            writer.WriteElement(new InlineString(new Text(value ?? string.Empty)));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
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
