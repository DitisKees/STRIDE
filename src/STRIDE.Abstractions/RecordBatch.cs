using NetTopologySuite.Geometries;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace STRIDE.Abstractions;

public sealed class RecordBatch : IRecordBatch
{
    private readonly object?[] _columns;

    public RecordBatch(Schema schema, int rowCount, object?[] columns)
    {
        Schema = schema;
        RowCount = rowCount;
        _columns = columns;

        if (columns.Length != schema.Fields.Length)
        {
            throw new ArgumentException("Column count must match schema field count.", nameof(columns));
        }
    }

    public Schema Schema { get; }

    public int RowCount { get; }

    public ReadOnlySpan<T> Column<T>(int ordinal)
        where T : unmanaged
    {
        if (_columns[ordinal] is T[] column)
        {
            return column;
        }

        throw new InvalidOperationException($"Column at ordinal {ordinal} is not a primitive '{typeof(T).Name}' column.");
    }

    public Utf8StringColumn StringColumn(int ordinal)
    {
        if (_columns[ordinal] is Utf8StringColumn column)
        {
            return column;
        }

        throw new InvalidOperationException($"Column at ordinal {ordinal} is not a string column.");
    }

    public GeometryColumn GeometryColumn(int ordinal)
    {
        if (_columns[ordinal] is GeometryColumn column)
        {
            return column;
        }

        throw new InvalidOperationException($"Column at ordinal {ordinal} is not a geometry column.");
    }

    public void Dispose()
    {
    }

    public string GetValueAsString(int ordinal, int rowIndex)
    {
        var field = Schema.Fields[ordinal];
        return field.Type switch
        {
            FieldType.Boolean => Column<bool>(ordinal)[rowIndex] ? "true" : "false",
            FieldType.Int32 => Column<int>(ordinal)[rowIndex].ToString(CultureInfo.InvariantCulture),
            FieldType.Int64 => Column<long>(ordinal)[rowIndex].ToString(CultureInfo.InvariantCulture),
            FieldType.Float64 => Column<double>(ordinal)[rowIndex].ToString(CultureInfo.InvariantCulture),
            FieldType.Utf8String => StringColumn(ordinal).GetString(rowIndex),
            _ => string.Empty,
        };
    }

    public static RecordBatch FromUtf8Rows(Schema schema, IReadOnlyList<string[]> rows)
        => FromRows(schema, rows);

    public static Utf8StringColumn CreateUtf8Column(IReadOnlyList<string?> values)
    {
        var rows = new string[values.Count][];
        for (var i = 0; i < values.Count; i++)
        {
            rows[i] = [values[i] ?? string.Empty];
        }

        return BuildUtf8Column(rows, 0);
    }

    public static RecordBatch FromRows(Schema schema, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0)
        {
            var emptyColumns = new object?[schema.Fields.Length];
            for (var c = 0; c < schema.Fields.Length; c++)
            {
                emptyColumns[c] = CreateEmptyColumn(schema.Fields[c].Type);
            }

            return new RecordBatch(schema, 0, emptyColumns);
        }

        var columnCount = schema.Fields.Length;
        var rowCount = rows.Count;
        var columns = new object?[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            var field = schema.Fields[c];
            switch (field.Type)
            {
                case FieldType.Int32:
                    {
                        var typed = new int[rowCount];
                        for (var r = 0; r < rowCount; r++)
                        {
                            var value = c < rows[r].Length ? rows[r][c] : string.Empty;
                            if (!string.IsNullOrWhiteSpace(value)
                                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            {
                                typed[r] = parsed;
                            }
                        }

                        columns[c] = typed;
                        break;
                    }

                case FieldType.Int64:
                    {
                        var typed = new long[rowCount];
                        for (var r = 0; r < rowCount; r++)
                        {
                            var value = c < rows[r].Length ? rows[r][c] : string.Empty;
                            if (!string.IsNullOrWhiteSpace(value)
                                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            {
                                typed[r] = parsed;
                            }
                        }

                        columns[c] = typed;
                        break;
                    }

                case FieldType.Float64:
                    {
                        var typed = new double[rowCount];
                        for (var r = 0; r < rowCount; r++)
                        {
                            var value = c < rows[r].Length ? rows[r][c] : string.Empty;
                            if (!string.IsNullOrWhiteSpace(value)
                                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                            {
                                typed[r] = parsed;
                            }
                        }

                        columns[c] = typed;
                        break;
                    }

                case FieldType.Boolean:
                    {
                        var typed = new bool[rowCount];
                        for (var r = 0; r < rowCount; r++)
                        {
                            var value = c < rows[r].Length ? rows[r][c] : string.Empty;
                            if (!string.IsNullOrWhiteSpace(value)
                                && bool.TryParse(value, out var parsed))
                            {
                                typed[r] = parsed;
                            }
                        }

                        columns[c] = typed;
                        break;
                    }

                default:
                    columns[c] = BuildUtf8Column(rows, c);
                    break;
            }
        }

        return new RecordBatch(schema, rowCount, columns);
    }

    public RecordBatch SelectRows(ReadOnlySpan<int> rowIndexes)
    {
        var outputColumns = new object?[Schema.Fields.Length];
        for (var c = 0; c < Schema.Fields.Length; c++)
        {
            switch (Schema.Fields[c].Type)
            {
                case FieldType.Int32:
                    {
                        var source = Column<int>(c);
                        var target = new int[rowIndexes.Length];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            target[r] = source[rowIndexes[r]];
                        }

                        outputColumns[c] = target;
                        break;
                    }

                case FieldType.Int64:
                    {
                        var source = Column<long>(c);
                        var target = new long[rowIndexes.Length];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            target[r] = source[rowIndexes[r]];
                        }

                        outputColumns[c] = target;
                        break;
                    }

                case FieldType.Float64:
                    {
                        var source = Column<double>(c);
                        var target = new double[rowIndexes.Length];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            target[r] = source[rowIndexes[r]];
                        }

                        outputColumns[c] = target;
                        break;
                    }

                case FieldType.Boolean:
                    {
                        var source = Column<bool>(c);
                        var target = new bool[rowIndexes.Length];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            target[r] = source[rowIndexes[r]];
                        }

                        outputColumns[c] = target;
                        break;
                    }

                case FieldType.Geometry:
                    {
                        var source = GeometryColumn(c).Values;
                        var target = new Geometry?[rowIndexes.Length];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            target[r] = source[rowIndexes[r]];
                        }

                        outputColumns[c] = new GeometryColumn(target);
                        break;
                    }

                default:
                    {
                        var source = StringColumn(c);
                        var rows = new string[rowIndexes.Length][];
                        for (var r = 0; r < rowIndexes.Length; r++)
                        {
                            rows[r] = [source.GetString(rowIndexes[r])];
                        }

                        outputColumns[c] = BuildUtf8Column(rows, 0);
                        break;
                    }
            }
        }

        return new RecordBatch(Schema, rowIndexes.Length, outputColumns);
    }

    private static object CreateEmptyColumn(FieldType type)
        => type switch
        {
            FieldType.Int32 => Array.Empty<int>(),
            FieldType.Int64 => Array.Empty<long>(),
            FieldType.Float64 => Array.Empty<double>(),
            FieldType.Boolean => Array.Empty<bool>(),
            _ => new Utf8StringColumn(ReadOnlyMemory<int>.Empty, ReadOnlyMemory<byte>.Empty),
        };

    private static Utf8StringColumn BuildUtf8Column(IReadOnlyList<string[]> rows, int columnIndex)
    {
        var rowCount = rows.Count;
        var offsets = ArrayPool<int>.Shared.Rent(rowCount + 1);
        offsets[0] = 0;

        var bytesPerRow = new byte[rowCount][];
        var totalBytes = 0;
        for (var r = 0; r < rowCount; r++)
        {
            var value = columnIndex < rows[r].Length ? rows[r][columnIndex] : string.Empty;
            var utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
            bytesPerRow[r] = utf8;
            totalBytes += utf8.Length;
            offsets[r + 1] = totalBytes;
        }

        var blob = ArrayPool<byte>.Shared.Rent(totalBytes);
        var cursor = 0;
        for (var r = 0; r < rowCount; r++)
        {
            var bytes = bytesPerRow[r];
            bytes.CopyTo(blob, cursor);
            cursor += bytes.Length;
        }

        var exactOffsets = new int[rowCount + 1];
        Array.Copy(offsets, exactOffsets, rowCount + 1);
        ArrayPool<int>.Shared.Return(offsets, clearArray: true);

        var exactBlob = new byte[totalBytes];
        Array.Copy(blob, exactBlob, totalBytes);
        ArrayPool<byte>.Shared.Return(blob, clearArray: true);

        return new Utf8StringColumn(exactOffsets, exactBlob);
    }
}
