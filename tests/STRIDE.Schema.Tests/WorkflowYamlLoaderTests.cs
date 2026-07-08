using STRIDE.Abstractions;

namespace STRIDE.Schema.Tests;

public class WorkflowYamlLoaderTests
{
    [Fact]
    public void LoadParsesWorkflowNodesAndSinks()
    {
        var yaml = string.Join('\n',
                "version: \"1.0\"",
                "name: \"sample\"",
                "settings:",
                "  batchSize: 250",
                "nodes:",
                "  - id: source",
                "    type: SourceCsv",
                "    params:",
                "      path: \"./input.csv\"",
                "  - id: filter",
                "    type: TransformFilter",
                "    inputs:",
                "      in: source",
                "    params:",
                "      when: \"id > 10\"",
                "    errorPolicy: Ignore",
                "sinks:",
                "  - id: sink",
                "    type: SinkCsv",
                "    inputs:",
                "      in: filter",
                "    params:",
                "      path: \"./output.csv\"");

        var loader = new STRIDE.Schema.WorkflowYamlLoader();
        var workflow = loader.Load(yaml);

        Assert.Equal("1.0", workflow.Version);
        Assert.Equal("sample", workflow.Name);
        Assert.Equal(250, workflow.Settings.BatchSize);
        Assert.Equal(2, workflow.Nodes.Count);
        Assert.Single(workflow.Sinks);
        Assert.Equal(ErrorPolicy.Ignore, workflow.Nodes[1].ErrorPolicy);
    }
}
