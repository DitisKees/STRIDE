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
    Null,
}