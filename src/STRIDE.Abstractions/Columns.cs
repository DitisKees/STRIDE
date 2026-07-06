using NetTopologySuite.Geometries;

namespace STRIDE.Abstractions;

// Arrow-style string representatie: één grote byte-blob met offsets
public readonly ref struct Utf8StringColumn
{
    public ReadOnlySpan<int> Offsets { get; }
    public ReadOnlySpan<byte> Data { get; }

    public Utf8StringColumn(ReadOnlySpan<int> offsets, ReadOnlySpan<byte> data)
    {
        Offsets = offsets;
        Data = data;
    }

    public ReadOnlySpan<byte> GetRowSpan(int rowIndex)
    {
        int start = Offsets[rowIndex];
        int end = Offsets[rowIndex + 1];
        return Data[start..end];
    }
}

public readonly ref struct GeometryColumn
{
    public ReadOnlySpan<Geometry> Geometries { get; }

    public GeometryColumn(ReadOnlySpan<Geometry> geometries)
    {
        Geometries = geometries;
    }
}