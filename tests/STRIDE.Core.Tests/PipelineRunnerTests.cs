using STRIDE.Abstractions;
using STRIDE.Blocks;
using STRIDE.Core;
using STRIDE.Schema;

namespace STRIDE.Core.Tests;

public class PipelineRunnerTests
{
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

    [Fact]
    public async Task PipelineRunnerStopBranchRollsBackTransactionalSinkAndAllowsOtherBranchToCommit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var brokenInputPath = Path.Combine(tempRoot, "broken-input.csv");
        var healthyInputPath = Path.Combine(tempRoot, "healthy-input.csv");
        var brokenSinkPath = Path.Combine(tempRoot, "broken-output.csv");
        var healthySinkPath = Path.Combine(tempRoot, "healthy-output.csv");

        await File.WriteAllLinesAsync(brokenInputPath,
        [
            "id,value",
            "1,10",
            "2,20",
        ]);

        await File.WriteAllLinesAsync(healthyInputPath,
        [
            "id,name",
            "10,a",
            "11,b",
        ]);

        var workflow = new WorkflowDefinition
        {
            Version = "1.0",
            Name = "stopbranch-transactional-sink",
            Settings = new WorkflowSettings
            {
                BatchSize = 1,
            },
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "broken-source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = brokenInputPath,
                        ["hasHeader"] = "true",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "broken-transform",
                    Type = "TransformCalculator",
                    Inputs = new Dictionary<string, string>
                    {
                        ["in"] = "broken-source",
                    },
                    Params = new Dictionary<string, string>
                    {
                        ["expression"] = "missing + 1",
                        ["outputField"] = "computed",
                    },
                    ErrorPolicy = ErrorPolicy.StopBranch,
                },
                new WorkflowNodeDefinition
                {
                    Id = "healthy-source",
                    Type = "SourceCsv",
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = healthyInputPath,
                        ["hasHeader"] = "true",
                    },
                },
            ],
            Sinks =
            [
                new WorkflowNodeDefinition
                {
                    Id = "broken-sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string>
                    {
                        ["in"] = "broken-transform",
                    },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = brokenSinkPath,
                        ["writeMode"] = "Transactional",
                    },
                },
                new WorkflowNodeDefinition
                {
                    Id = "healthy-sink",
                    Type = "SinkCsv",
                    Inputs = new Dictionary<string, string>
                    {
                        ["in"] = "healthy-source",
                    },
                    Params = new Dictionary<string, string>
                    {
                        ["path"] = healthySinkPath,
                        ["includeHeader"] = "true",
                        ["writeMode"] = "Transactional",
                    },
                },
            ],
        };

        var runner = new PipelineRunner();
        var exitCode = await runner.RunAsync(workflow, new GeneratedBlockFactory(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(brokenSinkPath));
        Assert.True(File.Exists(healthySinkPath));

        var healthyLines = await File.ReadAllLinesAsync(healthySinkPath);
        Assert.Equal(3, healthyLines.Length);
        Assert.Equal("id,name", healthyLines[0]);
        Assert.Contains("10,a", healthyLines);
        Assert.Contains("11,b", healthyLines);
    }
}
