using STRIDE.Blocks;
using STRIDE.Core;
using STRIDE.Schema;

if (args.Length == 0)
{
    Console.WriteLine("Usage: STRIDE.Cli <workflow.yaml>");
    Environment.ExitCode = 0;
    return;
}

var workflowPath = args[0];
if (!File.Exists(workflowPath))
{
    await Console.Error.WriteLineAsync($"Workflow file not found: {workflowPath}");
    Environment.ExitCode = 1;
    return;
}

var yaml = await File.ReadAllTextAsync(workflowPath).ConfigureAwait(false);
var secretResolver = new SecretResolver();
var resolvedYaml = secretResolver.ResolveScalars(
    yaml,
    static variableName => Environment.GetEnvironmentVariable(variableName));

var loader = new WorkflowYamlLoader();
var workflow = loader.Load(resolvedYaml);

using var drainCts = new CancellationTokenSource();
using var abortCts = new CancellationTokenSource();
var cancelPressCount = 0;
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    var count = Interlocked.Increment(ref cancelPressCount);
    if (count == 1)
    {
        Console.Error.WriteLine("Cancellation requested. Finishing in-flight batches and draining pipeline...");
        drainCts.Cancel();
        return;
    }

    if (count == 2)
    {
        Console.Error.WriteLine("Abort requested. Rolling back in-progress transactional sinks...");
        abortCts.Cancel();
        return;
    }

    Console.Error.WriteLine("Forced shutdown requested.");
    Environment.Exit(130);
};

var runner = new PipelineRunner();
var blockFactory = new GeneratedBlockFactory();
var exitCode = await runner.RunAsync(workflow, blockFactory, drainCts.Token, abortCts.Token).ConfigureAwait(false);
Environment.ExitCode = exitCode;
