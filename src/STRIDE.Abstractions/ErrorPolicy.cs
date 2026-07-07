namespace STRIDE.Abstractions;

public enum ErrorPolicy : byte
{
    StopPipeline,
    StopBranch,
    Ignore,
}