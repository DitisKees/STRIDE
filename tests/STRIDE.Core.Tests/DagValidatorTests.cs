using STRIDE.Abstractions;
using STRIDE.Blocks;
using STRIDE.Schema;

namespace STRIDE.Core.Tests;

public class DagValidatorTests
{
    [Fact]
    public void DagValidatorDetectsCycle()
    {
        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "cycle",
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string> { ["path"] = "input.csv" },
                },
                new WorkflowNodeDefinition
                {
                    Id = "filter",
                    Type = "TransformFilter",
                    Inputs = new Dictionary<string, string> { ["in"] = "sink" },
                    Params = new Dictionary<string, string> { ["when"] = "id > 1" },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string> { ["in"] = "filter" },
                    Params = new Dictionary<string, string> { ["path"] = "out.csv" },
                },
            ],
        };

        var validator = new DagValidator();
        var factory = new GeneratedBlockFactory();

        var exception = Assert.Throws<DagValidationException>(() => validator.Validate(workflow, factory));
        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
