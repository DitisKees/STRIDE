namespace STRIDE.Abstractions;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StrideBlockAttribute : Attribute
{
    public StrideBlockAttribute(string type)
    {
        Type = type;
    }

    public string Type { get; }
}