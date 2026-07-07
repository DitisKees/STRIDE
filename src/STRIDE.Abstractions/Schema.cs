using System.Collections.Immutable;

namespace STRIDE.Abstractions;

public sealed class Schema
{
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    public Schema(ImmutableArray<FieldDef> fields)
    {
        Fields = fields;
        GeometryFieldIndex = -1;

        var ordinals = new Dictionary<string, int>(fields.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Length; i++)
        {
            if (GeometryFieldIndex == -1 && fields[i].Type == FieldType.Geometry)
            {
                GeometryFieldIndex = i;
            }

            ordinals[fields[i].Name] = i;
        }

        _ordinals = ordinals;
    }

    public ImmutableArray<FieldDef> Fields { get; }

    public int GeometryFieldIndex { get; }

    public bool TryGetOrdinal(string name, out int ordinal)
        => _ordinals.TryGetValue(name, out ordinal);
}