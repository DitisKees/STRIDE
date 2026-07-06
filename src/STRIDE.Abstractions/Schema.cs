using System.Collections.Immutable;

namespace STRIDE.Abstractions;

public sealed class Schema
{
    private readonly Dictionary<string, int> _nameToOrdinal;

    public ImmutableArray<FieldDef> Fields { get; }
    public int GeometryFieldIndex { get; }

    public Schema(ImmutableArray<FieldDef> fields)
    {
        Fields = fields;
        _nameToOrdinal = new Dictionary<string, int>(fields.Length, StringComparer.Ordinal);

        int geoIndex = -1;
        for (int i = 0; i < fields.Length; i++)
        {
            _nameToOrdinal[fields[i].Name] = i;
            if (fields[i].Type == FieldType.Geometry && geoIndex == -1)
            {
                geoIndex = i; // Eerste geometrie-kolom identificeren
            }
        }
        GeometryFieldIndex = geoIndex;
    }

    public bool TryGetOrdinal(string name, out int ordinal)
    {
        return _nameToOrdinal.TryGetValue(name, out ordinal);
    }
}