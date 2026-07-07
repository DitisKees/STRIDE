using NetTopologySuite.Geometries;

namespace STRIDE.Abstractions;

public readonly struct GeometryColumn
{
    public GeometryColumn(IReadOnlyList<Geometry?> values)
    {
        Values = values;
    }

    public IReadOnlyList<Geometry?> Values { get; }
}