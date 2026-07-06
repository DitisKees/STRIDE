namespace STRIDE.Abstractions;

public enum ErrorPolicy
{
    StopPipeline,
    StopBranch,
    Ignore
}

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StrideBlockAttribute(string typeString) : Attribute
{
    public string TypeString { get; } = typeString;
}

public sealed class BlockContext(string nodeId, ErrorPolicy errorPolicy, Action<string, Exception> errorLogger)
{
    public string NodeId { get; } = nodeId;
    public ErrorPolicy ConfiguredErrorPolicy { get; } = errorPolicy;

    // Abstracties voor logging en runtime error dumping
    public Action<string, Exception> ErrorLogger { get; } = errorLogger;
}