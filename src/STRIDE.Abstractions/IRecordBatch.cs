namespace STRIDE.Abstractions;

public interface IRecordBatch : IDisposable
{
    Schema Schema { get; }
    int RowCount { get; }

    // Null-controle per cel
    bool IsNull(int ordinal, int rowIndex);

    // Primitives via high-performance zero-copy memory
    ReadOnlyMemory<T> GetColumnMemory<T>(int ordinal) where T : unmanaged;

    // Geavanceerde kolom-uitlezing via on-stack spans
    Utf8StringColumn GetStringColumn(int ordinal);
    GeometryColumn GetGeometryColumn(int ordinal);
}