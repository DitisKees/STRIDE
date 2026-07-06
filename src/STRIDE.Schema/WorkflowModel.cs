namespace STRIDE.Schema;

public sealed record WorkflowConfig(
    string Version,
    string Name,
    Dictionary<string, string> Settings,
    List<WorkflowNode> Nodes,
    List<WorkflowSink> Sinks
);

public sealed record WorkflowNode(
    string Id,
    string Type,
    Dictionary<string, string>? Inputs,
    Dictionary<string, string> Params,
    string? ErrorPolicy
);

public sealed record WorkflowSink(
    string Id,
    string Type,
    Dictionary<string, string> Inputs,
    Dictionary<string, string> Params,
    string? WriteMode
);