namespace STRIDE.Abstractions;

public interface IRecordBatch : IDisposable
{
    Schema Schema { get; }

    int RowCount { get; }

    ReadOnlySpan<T> Column<T>(int ordinal)
        where T : unmanaged;

    Utf8StringColumn StringColumn(int ordinal);

    GeometryColumn GeometryColumn(int ordinal);

    string GetValueAsString(int ordinal, int rowIndex);
}