using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using STRIDE.Abstractions;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("SourceExcel")]
public sealed class SourceExcelBlock(
    string path,
    string? sheetName = null,
    bool hasHeader = true,
    int batchSize = 1000,
    int schemaSampleRows = 256,
    string geometryColumn = "geom") : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly int _schemaSampleRows = Math.Max(1, schemaSampleRows);
    private STRIDE.Abstractions.Schema? _cachedSchema;
    private string[]? _columnNames;

    public STRIDE.Abstractions.Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Excel workbook is missing WorkbookPart.");
        var worksheetPart = ResolveWorksheetPart(workbookPart);
        var sharedStrings = ReadSharedStrings(workbookPart);

        var sampledRows = new List<CatalogFeatureRecord>(_schemaSampleRows);
        using var openXmlReader = OpenXmlReader.Create(worksheetPart);

        var isFirstRow = true;
        while (openXmlReader.Read() && sampledRows.Count < _schemaSampleRows)
        {
            if (openXmlReader.ElementType != typeof(Row))
            {
                continue;
            }

            if (openXmlReader.LoadCurrentElement() is not Row row)
            {
                continue;
            }

            var values = ReadRow(row, sharedStrings);

            if (isFirstRow && hasHeader)
            {
                _columnNames = values
                    .Select((value, index) => string.IsNullOrWhiteSpace(value)
                        ? $"col{index + 1}"
                        : value.Trim())
                    .ToArray();

                isFirstRow = false;
                continue;
            }

            if (isFirstRow)
            {
                _columnNames = Enumerable.Range(1, values.Length)
                    .Select(static i => $"col{i}")
                    .ToArray();
                isFirstRow = false;
            }

            sampledRows.Add(ToFeatureRecord(values, _columnNames!));
        }

        _columnNames ??= ["value"];
        _cachedSchema = CatalogRecordUtilities.BuildSchema(sampledRows, geometryColumn);
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Excel workbook is missing WorkbookPart.");
        var worksheetPart = ResolveWorksheetPart(workbookPart);
        var sharedStrings = ReadSharedStrings(workbookPart);

        var buffer = new List<CatalogFeatureRecord>(_batchSize);
        using var openXmlReader = OpenXmlReader.Create(worksheetPart);
        var isFirstRow = true;

        while (openXmlReader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (openXmlReader.ElementType != typeof(Row))
            {
                continue;
            }

            if (openXmlReader.LoadCurrentElement() is not Row row)
            {
                continue;
            }

            var values = ReadRow(row, sharedStrings);

            if (isFirstRow && hasHeader)
            {
                isFirstRow = false;
                continue;
            }

            isFirstRow = false;
            buffer.Add(ToFeatureRecord(values, _columnNames!));
            if (buffer.Count < _batchSize)
            {
                continue;
            }

            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
            buffer.Clear();
            await Task.Yield();
        }

        if (buffer.Count > 0)
        {
            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
        }
    }

    private WorksheetPart ResolveWorksheetPart(WorkbookPart workbookPart)
    {
        var workbook = workbookPart.Workbook
            ?? throw new InvalidOperationException("Excel workbook is missing workbook metadata.");

        var sheets = workbook.Sheets?.Elements<Sheet>()
            ?? throw new InvalidOperationException("Excel workbook does not contain sheets.");

        var targetSheet = string.IsNullOrWhiteSpace(sheetName)
            ? sheets.FirstOrDefault()
            : sheets.FirstOrDefault(sheet => string.Equals(sheet.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));

        var sheetId = targetSheet?.Id?.Value;
        if (string.IsNullOrWhiteSpace(sheetId))
        {
            throw new InvalidOperationException("Target worksheet was not found in Excel workbook.");
        }

        return (WorksheetPart)workbookPart.GetPartById(sheetId);
    }

    private static IReadOnlyList<string> ReadSharedStrings(WorkbookPart workbookPart)
    {
        var sharedStringPart = workbookPart.SharedStringTablePart;
        if (sharedStringPart?.SharedStringTable is null)
        {
            return Array.Empty<string>();
        }

        return sharedStringPart.SharedStringTable
            .Elements<SharedStringItem>()
            .Select(static item => item.InnerText)
            .ToArray();
    }

    private static string?[] ReadRow(Row row, IReadOnlyList<string> sharedStrings)
    {
        var indexed = new SortedDictionary<int, string?>();

        foreach (var cell in row.Elements<Cell>())
        {
            var index = ParseColumnIndex(cell.CellReference?.Value);
            indexed[index] = ResolveCellValue(cell, sharedStrings);
        }

        if (indexed.Count == 0)
        {
            return Array.Empty<string?>();
        }

        var maxIndex = indexed.Keys.Max();
        var values = new string?[maxIndex + 1];
        foreach (var (index, value) in indexed)
        {
            values[index] = value;
        }

        return values;
    }

    private static int ParseColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return 0;
        }

        var index = 0;
        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return Math.Max(0, index - 1);
    }

    private static string? ResolveCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (!int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex))
            {
                return cell.CellValue?.Text;
            }

            return sharedIndex >= 0 && sharedIndex < sharedStrings.Count
                ? sharedStrings[sharedIndex]
                : null;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        return cell.CellValue?.Text;
    }

    private CatalogFeatureRecord ToFeatureRecord(IReadOnlyList<string?> row, IReadOnlyList<string> columnNames)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Geometry? geometry = null;

        for (var index = 0; index < columnNames.Count; index++)
        {
            var value = index < row.Count ? row[index] : null;
            var key = columnNames[index];

            if (string.Equals(key, geometryColumn, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    geometry = new WKTReader().Read(value);
                    continue;
                }
                catch
                {
                    // Treat invalid WKT as plain attribute value.
                }
            }

            attributes[key] = CatalogRecordUtilities.ParseScalarString(value);
        }

        return new CatalogFeatureRecord(attributes, geometry);
    }
}
