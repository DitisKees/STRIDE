using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace STRIDE.Abstractions;

public enum FieldType : byte
{
    Boolean,
    Int32,
    Int64,
    Float64,
    Utf8String,
    DateTimeUtc,
    Geometry,
    Null
}

public sealed record FieldDef(string Name, FieldType Type, bool Nullable);
