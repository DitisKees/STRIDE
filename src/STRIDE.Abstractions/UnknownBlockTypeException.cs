namespace STRIDE.Abstractions;

public sealed class UnknownBlockTypeException : Exception
{
    public UnknownBlockTypeException(string type)
        : base($"Unknown block type '{type}'.")
    {
        Type = type;
    }

    public string Type { get; }
}