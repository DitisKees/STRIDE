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
    Console.Error.WriteLine($"Workflow file not found: {workflowPath}");
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

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var runner = new PipelineRunner();
var blockFactory = new GeneratedBlockFactory();
var exitCode = await runner.RunAsync(workflow, blockFactory, cts.Token).ConfigureAwait(false);
Environment.ExitCode = exitCode;
