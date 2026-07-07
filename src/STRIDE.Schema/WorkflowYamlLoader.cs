using STRIDE.Abstractions;
using YamlDotNet.RepresentationModel;

namespace STRIDE.Schema;

public sealed class WorkflowYamlLoader
{
    public WorkflowDefinition Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("The workflow YAML must contain a root mapping node.");
        }

        return new WorkflowDefinition
        {
            Version = RequiredScalar(root, "version"),
            Name = RequiredScalar(root, "name"),
            Settings = ReadSettings(OptionalMapping(root, "settings")),
            Nodes = ReadNodeList(OptionalSequence(root, "nodes")),
            Sinks = ReadNodeList(OptionalSequence(root, "sinks")),
        };
    }

    private static WorkflowSettings ReadSettings(YamlMappingNode? node)
    {
        if (node is null)
        {
            return new WorkflowSettings();
        }

        return new WorkflowSettings
        {
            BatchSize = OptionalInt(node, "batchSize") ?? 1000,
            MaxDegreeOfParallelism = OptionalInt(node, "maxDegreeOfParallelism") ?? Environment.ProcessorCount,
            SpillDirectory = OptionalScalar(node, "spillDirectory") ?? "./.stride-spill",
            SpillThresholdBytes = OptionalLong(node, "spillThresholdBytes") ?? 1_073_741_824L,
            ErrorLog = OptionalScalar(node, "errorLog") ?? "./output/error_log.ndjson",
        };
    }

    private static IReadOnlyList<WorkflowNodeDefinition> ReadNodeList(YamlSequenceNode? sequence)
    {
        if (sequence is null)
        {
            return Array.Empty<WorkflowNodeDefinition>();
        }

        var nodes = new List<WorkflowNodeDefinition>(sequence.Children.Count);
        foreach (var child in sequence.Children)
        {
            if (child is not YamlMappingNode node)
            {
                throw new InvalidOperationException("Each node entry must be a mapping.");
            }

            nodes.Add(new WorkflowNodeDefinition
            {
                Id = RequiredScalar(node, "id"),
                Type = RequiredScalar(node, "type"),
                Inputs = ReadStringMap(OptionalMapping(node, "inputs")),
                Params = ReadStringMap(OptionalMapping(node, "params")),
                ErrorPolicy = ReadErrorPolicy(OptionalScalar(node, "errorPolicy")),
            });
        }

        return nodes;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(YamlMappingNode? node)
    {
        if (node is null)
        {
            return new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        var output = new Dictionary<string, string>(node.Children.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in node.Children)
        {
            var key = (item.Key as YamlScalarNode)?.Value ?? throw new InvalidOperationException("Mapping keys must be scalar values.");
            var value = (item.Value as YamlScalarNode)?.Value ?? throw new InvalidOperationException("Only scalar mapping values are supported in this phase.");
            output[key] = value ?? string.Empty;
        }

        return output;
    }

    private static ErrorPolicy ReadErrorPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ErrorPolicy.StopPipeline;
        }

        return value switch
        {
            "StopPipeline" => ErrorPolicy.StopPipeline,
            "StopBranch" => ErrorPolicy.StopBranch,
            "Ignore" => ErrorPolicy.Ignore,
            _ => throw new InvalidOperationException($"Unknown ErrorPolicy '{value}'."),
        };
    }

    private static string RequiredScalar(YamlMappingNode root, string key)
        => OptionalScalar(root, key) ?? throw new InvalidOperationException($"Missing required scalar '{key}'.");

    private static string? OptionalScalar(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var valueNode))
        {
            return null;
        }

        return (valueNode as YamlScalarNode)?.Value;
    }

    private static int? OptionalInt(YamlMappingNode root, string key)
    {
        var value = OptionalScalar(root, key);
        return value is null ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long? OptionalLong(YamlMappingNode root, string key)
    {
        var value = OptionalScalar(root, key);
        return value is null ? null : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static YamlMappingNode? OptionalMapping(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var valueNode))
        {
            return null;
        }

        return valueNode as YamlMappingNode;
    }

    private static YamlSequenceNode? OptionalSequence(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var valueNode))
        {
            return null;
        }

        return valueNode as YamlSequenceNode;
    }
}