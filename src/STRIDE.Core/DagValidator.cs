using STRIDE.Abstractions;
using STRIDE.Schema;

namespace STRIDE.Core;

public sealed class DagValidator
{
    private static readonly IReadOnlyDictionary<string, int> RequiredInputCountByType =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TransformSpatialJoin"] = 2,
            ["TransformDifference"] = 2,
            ["TransformSnapGeometries"] = 2,
        };

    public DagValidationResult Validate(WorkflowDefinition workflow, IBlockFactory blockFactory)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(blockFactory);

        var errors = new List<string>();
        var nodeById = BuildNodeMap(workflow, errors);

        ValidateTypes(nodeById.Values, blockFactory, errors);
        var inputMap = BuildInputMap(nodeById, errors);
        ValidateInputCountRules(nodeById.Values, errors);

        var topologicalOrder = TopologicalSort(nodeById, inputMap, errors);
        var schemas = PropagateSchemas(topologicalOrder, nodeById, inputMap, blockFactory, errors);

        if (errors.Count > 0)
        {
            throw new DagValidationException(errors);
        }

        return new DagValidationResult(nodeById, inputMap, topologicalOrder, schemas);
    }

    private static Dictionary<string, WorkflowNodeDefinition> BuildNodeMap(
        WorkflowDefinition workflow,
        List<string> errors)
    {
        var nodeById = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);

        foreach (var node in workflow.Nodes.Concat(workflow.Sinks))
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                errors.Add("Node id cannot be empty.");
                continue;
            }

            if (!nodeById.TryAdd(node.Id, node))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }

        if (workflow.Sinks.Count == 0)
        {
            errors.Add("Workflow must define at least one sink.");
        }

        return nodeById;
    }

    private static void ValidateTypes(
        IEnumerable<WorkflowNodeDefinition> nodes,
        IBlockFactory blockFactory,
        List<string> errors)
    {
        foreach (var node in nodes)
        {
            if (!blockFactory.RegisteredTypes.Contains(node.Type))
            {
                errors.Add($"Node '{node.Id}' uses unknown block type '{node.Type}'.");
                continue;
            }

            try
            {
                _ = blockFactory.Create(node.Type, BlockParams.FromStringMap(node.Params));
            }
            catch (Exception ex)
            {
                errors.Add($"Node '{node.Id}' has invalid parameters: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, Dictionary<string, InputReference>> BuildInputMap(
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodeById,
        List<string> errors)
    {
        var map = new Dictionary<string, Dictionary<string, InputReference>>(StringComparer.Ordinal);

        foreach (var node in nodeById.Values)
        {
            var nodeInputs = new Dictionary<string, InputReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in node.Inputs)
            {
                var reference = ParseReference(input.Value);
                if (!nodeById.ContainsKey(reference.UpstreamNodeId))
                {
                    errors.Add($"Node '{node.Id}' input port '{input.Key}' references unknown upstream node '{reference.UpstreamNodeId}'.");
                    continue;
                }

                nodeInputs[input.Key] = reference;
            }

            map[node.Id] = nodeInputs;
        }

        return map;
    }

    private static void ValidateInputCountRules(IEnumerable<WorkflowNodeDefinition> nodes, List<string> errors)
    {
        foreach (var node in nodes)
        {
            var hasRequiredInput = node.Type.StartsWith("Transform", StringComparison.Ordinal)
                || node.Type.StartsWith("Sink", StringComparison.Ordinal);

            if (hasRequiredInput && node.Inputs.Count == 0)
            {
                errors.Add($"Node '{node.Id}' ({node.Type}) must define at least one input.");
                continue;
            }

            if (RequiredInputCountByType.TryGetValue(node.Type, out var requiredCount)
                && node.Inputs.Count < requiredCount)
            {
                errors.Add($"Node '{node.Id}' ({node.Type}) requires at least {requiredCount} inputs.");
            }
        }
    }

    private static IReadOnlyList<string> TopologicalSort(
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodeById,
        IReadOnlyDictionary<string, Dictionary<string, InputReference>> inputs,
        List<string> errors)
    {
        var indegree = nodeById.ToDictionary(static kvp => kvp.Key, static _ => 0, StringComparer.Ordinal);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in nodeById.Values)
        {
            adjacency[node.Id] = new List<string>();
        }

        foreach (var node in nodeById.Values)
        {
            foreach (var reference in inputs[node.Id].Values)
            {
                adjacency[reference.UpstreamNodeId].Add(node.Id);
                indegree[node.Id]++;
            }
        }

        var queue = new Queue<string>(indegree.Where(static x => x.Value == 0).Select(static x => x.Key));
        var order = new List<string>(nodeById.Count);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            order.Add(nodeId);

            foreach (var downstream in adjacency[nodeId])
            {
                indegree[downstream]--;
                if (indegree[downstream] == 0)
                {
                    queue.Enqueue(downstream);
                }
            }
        }

        if (order.Count != nodeById.Count)
        {
            var cycleNodes = indegree.Where(static kv => kv.Value > 0).Select(static kv => kv.Key);
            errors.Add($"Workflow graph contains at least one cycle involving: {string.Join(", ", cycleNodes)}");
        }

        return order;
    }

    private static Dictionary<string, STRIDE.Abstractions.Schema> PropagateSchemas(
        IReadOnlyList<string> topologicalOrder,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodeById,
        IReadOnlyDictionary<string, Dictionary<string, InputReference>> inputMap,
        IBlockFactory blockFactory,
        List<string> errors)
    {
        var outputSchemas = new Dictionary<string, STRIDE.Abstractions.Schema>(StringComparer.Ordinal);

        foreach (var nodeId in topologicalOrder)
        {
            var node = nodeById[nodeId];
            var instance = blockFactory.Create(node.Type, BlockParams.FromStringMap(node.Params));

            try
            {
                switch (instance)
                {
                    case ISourceBlock sourceBlock:
                        outputSchemas[nodeId] = sourceBlock.DeriveOutputSchema();
                        break;

                    case ITransformBlock transformBlock:
                        var inputSchemas = new Dictionary<string, STRIDE.Abstractions.Schema>(StringComparer.OrdinalIgnoreCase);
                        foreach (var input in inputMap[nodeId])
                        {
                            if (!outputSchemas.TryGetValue(input.Value.UpstreamNodeId, out var upstreamSchema))
                            {
                                errors.Add($"Schema propagation failed for node '{nodeId}' because upstream schema for '{input.Value.UpstreamNodeId}' is not available.");
                                continue;
                            }

                            inputSchemas[input.Key] = upstreamSchema;
                        }

                        if (!errors.Any())
                        {
                            outputSchemas[nodeId] = transformBlock.DeriveOutputSchema(inputSchemas);
                        }

                        break;

                    case ISinkBlock:
                        break;

                    default:
                        errors.Add($"Node '{nodeId}' type '{node.Type}' does not implement a supported block interface.");
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Schema propagation failed for node '{nodeId}': {ex.Message}");
            }
        }

        return outputSchemas;
    }

    internal static InputReference ParseReference(string value)
    {
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 1
            ? new InputReference(parts[0], "out")
            : new InputReference(parts[0], parts[1]);
    }
}

public sealed record DagValidationResult(
    IReadOnlyDictionary<string, WorkflowNodeDefinition> NodeById,
    IReadOnlyDictionary<string, Dictionary<string, InputReference>> Inputs,
    IReadOnlyList<string> TopologicalOrder,
    IReadOnlyDictionary<string, STRIDE.Abstractions.Schema> OutputSchemas);

public sealed record InputReference(string UpstreamNodeId, string UpstreamPort);

public sealed class DagValidationException : Exception
{
    public DagValidationException(IReadOnlyList<string> errors)
        : base($"Workflow validation failed:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}