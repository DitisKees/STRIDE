namespace STRIDE.Abstractions;

public readonly struct Utf8StringColumn
{
    public Utf8StringColumn(ReadOnlyMemory<int> offsets, ReadOnlyMemory<byte> utf8Data)
    {
        Offsets = offsets;
        Utf8Data = utf8Data;
    }

    public ReadOnlyMemory<int> Offsets { get; }

    public ReadOnlyMemory<byte> Utf8Data { get; }

    public ReadOnlySpan<byte> SliceForRow(int rowIndex)
    {
        var offsets = Offsets.Span;
        var start = offsets[rowIndex];
        var end = offsets[rowIndex + 1];
        return Utf8Data.Span[start..end];
    }

    public string GetString(int rowIndex)
        => System.Text.Encoding.UTF8.GetString(SliceForRow(rowIndex));
}