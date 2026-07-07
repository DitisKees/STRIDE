using STRIDE.Abstractions;

namespace STRIDE.Schema;

public sealed class WorkflowDefinition
{
    public required string Version { get; init; }

    public required string Name { get; init; }

    public WorkflowSettings Settings { get; init; } = new();

    public IReadOnlyList<WorkflowNodeDefinition> Nodes { get; init; } = Array.Empty<WorkflowNodeDefinition>();

    public IReadOnlyList<WorkflowNodeDefinition> Sinks { get; init; } = Array.Empty<WorkflowNodeDefinition>();
}

public sealed class WorkflowSettings
{
    public int BatchSize { get; init; } = 1000;

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    public string SpillDirectory { get; init; } = "./.stride-spill";

    public long SpillThresholdBytes { get; init; } = 1_073_741_824L;

    public string ErrorLog { get; init; } = "./output/error_log.ndjson";
}

public sealed class WorkflowNodeDefinition
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Params { get; init; } = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    public ErrorPolicy ErrorPolicy { get; init; } = ErrorPolicy.StopPipeline;
}