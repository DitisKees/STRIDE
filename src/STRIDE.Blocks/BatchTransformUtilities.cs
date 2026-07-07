using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

internal static class BatchTransformUtilities
{
    public static RecordBatch EnsureRecordBatch(IRecordBatch batch)
    {
        if (batch is RecordBatch recordBatch)
        {
            return recordBatch;
        }

        var rows = new string[batch.RowCount][];
        for (var row = 0; row < batch.RowCount; row++)
        {
            rows[row] = new string[batch.Schema.Fields.Length];
            for (var col = 0; col < batch.Schema.Fields.Length; col++)
            {
                rows[row][col] = batch.GetValueAsString(col, row);
            }
        }

        return RecordBatch.FromRows(batch.Schema, rows);
    }

    public static object?[] CopyColumns(IRecordBatch batch)
    {
        var columns = new object?[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            columns[col] = CopyColumn(batch, col);
        }

        return columns;
    }

    public static object CopyColumn(IRecordBatch batch, int ordinal)
    {
        var type = batch.Schema.Fields[ordinal].Type;
        return type switch
        {
            FieldType.Boolean => batch.Column<bool>(ordinal).ToArray(),
            FieldType.Int32 => batch.Column<int>(ordinal).ToArray(),
            FieldType.Int64 => batch.Column<long>(ordinal).ToArray(),
            FieldType.Float64 => batch.Column<double>(ordinal).ToArray(),
            FieldType.Geometry => new GeometryColumn(batch.GeometryColumn(ordinal).Values.ToArray()),
            _ => CopyStringColumn(batch, ordinal),
        };
    }

    public static Utf8StringColumn CopyStringColumn(IRecordBatch batch, int ordinal)
    {
        var source = batch.StringColumn(ordinal);
        var values = new string?[batch.RowCount];
        for (var row = 0; row < batch.RowCount; row++)
        {
            values[row] = source.GetString(row);
        }

        return RecordBatch.CreateUtf8Column(values);
    }
}
