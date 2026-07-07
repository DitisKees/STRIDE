using STRIDE.Abstractions;
using STRIDE.Blocks;
using STRIDE.Core;
using STRIDE.Schema;

namespace STRIDE.Core.Tests;

public class UnitTest1
{
    [Fact]
    public void ResolveScalarsReplacesTemplateVariablesInScalarNodes()
    {
        var yaml = """
        version: "1.0"
        settings:
          errorLog: "${LOG_PATH}"
        """;

        var resolver = new SecretResolver();
        var resolved = resolver.ResolveScalars(yaml, key => key == "LOG_PATH" ? "./logs/errors.ndjson" : null);

        Assert.Contains("./logs/errors.ndjson", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveScalarsThrowsWhenVariableIsMissingAndAllowEmptyFalse()
    {
        var yaml = "value: ${MISSING}";
        var resolver = new SecretResolver();

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveScalars(yaml, _ => null));
    }

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

    [Fact]
    public async Task PipelineRunnerExecutesCsvFilterToCsv()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var inputPath = Path.Combine(tempRoot, "input.csv");
        var outputPath = Path.Combine(tempRoot, "output.csv");
        await File.WriteAllLinesAsync(inputPath,
        [
            "id,status",
            "5,inactive",
            "15,active",
            "20,active",
        ]);

        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "csv-filter",
            Settings = new WorkflowSettings
            {
                BatchSize = 2,
            },
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = inputPath,
                        ["columns"] = "id,status",
                        ["hasHeader"] = "true",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "filter",
                    Type = "TransformFilter",
                    Inputs = new Dictionary<string, string>
                    {
                        ["in"] = "source",
                    },
                    Params = new Dictionary<string, string>
                    {
                        ["when"] = "id > 10",
                    },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string>
                    {
                        ["in"] = "filter",
                    },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = outputPath,
                        ["includeHeader"] = "true",
                    },
                },
            ],
        };

        var runner = new PipelineRunner();
        var exitCode = await runner.RunAsync(workflow, new GeneratedBlockFactory(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal("id,status", lines[0]);
        Assert.Contains("15,active", lines);
        Assert.Contains("20,active", lines);
        Assert.DoesNotContain("5,inactive", lines);
    }

    [Fact]
    public async Task PipelineRunnerSupportsBooleanAndStringOperators()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var inputPath = Path.Combine(tempRoot, "input.csv");
        var outputPath = Path.Combine(tempRoot, "output.csv");
        await File.WriteAllLinesAsync(inputPath,
        [
            "id,isActive,status",
            "1,true,active",
            "2,false,inactive",
            "3,true,archived",
        ]);

        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "csv-filter-bool-string",
            Settings = new WorkflowSettings { BatchSize = 2 },
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = inputPath,
                        ["hasHeader"] = "true",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "filterActive",
                    Type = "TransformFilter",
                    Inputs = new Dictionary<string, string> { ["in"] = "source" },
                    Params = new Dictionary<string, string> { ["when"] = "isActive == true" },
                },
                new WorkflowNodeDefinition
                {
                    Id = "filterStatus",
                    Type = "TransformFilter",
                    Inputs = new Dictionary<string, string> { ["in"] = "filterActive" },
                    Params = new Dictionary<string, string> { ["when"] = "status startsWith \"act\"" },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string> { ["in"] = "filterStatus" },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = outputPath,
                        ["includeHeader"] = "true",
                    },
                },
            ],
        };

        var runner = new PipelineRunner();
        var exitCode = await runner.RunAsync(workflow, new GeneratedBlockFactory(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal("id,isActive,status", lines[0]);
        Assert.Contains("1,true,active", lines);
        Assert.DoesNotContain("2,false,inactive", lines);
        Assert.DoesNotContain("3,true,archived", lines);
    }

    [Fact]
    public async Task PipelineRunnerSupportsLogicalCompositionOperators()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var inputPath = Path.Combine(tempRoot, "input.csv");
        var outputPath = Path.Combine(tempRoot, "output.csv");
        await File.WriteAllLinesAsync(inputPath,
        [
            "id,isActive,status",
            "1,true,active",
            "2,false,pending",
            "3,true,archived",
            "4,false,inactive",
        ]);

        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "csv-filter-logical",
            Settings = new WorkflowSettings { BatchSize = 2 },
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = inputPath,
                        ["hasHeader"] = "true",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "filter",
                    Type = "TransformFilter",
                    Inputs = new Dictionary<string, string> { ["in"] = "source" },
                    Params = new Dictionary<string, string>
                    {
                        ["when"] = "(isActive == true && status startsWith \"act\") || (!isActive == true && id == 2)",
                    },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string> { ["in"] = "filter" },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = outputPath,
                        ["includeHeader"] = "true",
                    },
                },
            ],
        };

        var runner = new PipelineRunner();
        var exitCode = await runner.RunAsync(workflow, new GeneratedBlockFactory(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal("id,isActive,status", lines[0]);
        Assert.Contains("1,true,active", lines);
        Assert.Contains("2,false,pending", lines);
        Assert.DoesNotContain("3,true,archived", lines);
        Assert.DoesNotContain("4,false,inactive", lines);
    }

    [Fact]
    public async Task PipelineRunnerExecutesAggregatorWithSpillThreshold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var inputPath = Path.Combine(tempRoot, "input.csv");
        var outputPath = Path.Combine(tempRoot, "output.csv");
        await File.WriteAllLinesAsync(inputPath,
        [
            "category,value",
            "A,10",
            "B,7",
            "A,5",
            "B,3",
            "A,5",
            "B,10",
        ]);

        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "csv-aggregate",
            Settings = new WorkflowSettings
            {
                BatchSize = 2,
                SpillDirectory = Path.Combine(tempRoot, "spill"),
            },
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = inputPath,
                        ["hasHeader"] = "true",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "aggregate",
                    Type = "TransformAggregator",
                    Inputs = new Dictionary<string, string> { ["in"] = "source" },
                    Params = new Dictionary<string, string>
                    {
                        ["groupBy"] = "category",
                        ["aggregates"] = "count:*:row_count;sum:value:total_value",
                        ["spillThresholdBytes"] = "1",
                    },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string> { ["in"] = "aggregate" },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = outputPath,
                        ["includeHeader"] = "true",
                    },
                },
            ],
        };

        var runner = new PipelineRunner();
        var exitCode = await runner.RunAsync(workflow, new GeneratedBlockFactory(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal("category,row_count,total_value", lines[0]);

        var rows = lines.Skip(1).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("A,3,20", rows[0]);
        Assert.Equal("B,3,20", rows[1]);
    }
}
