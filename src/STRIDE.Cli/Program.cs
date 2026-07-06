using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STRIDE.Core;
using STRIDE.Schema;

namespace STRIDE.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Controleer argumenten
        if (args.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Fout: Geen workflow-configuratiebestand meegegeven.");
            Console.ResetColor();
            Console.WriteLine("Gebruik: stride <pad-naar-workflow.yaml>");
            return 1;
        }

        string configPath = args[0];
        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fout: Bestand '{configPath}' niet gevonden.");
            Console.ResetColor();
            return 1;
        }

        // 2. Initialiseer high-performance console logging
        // Initialiseer high-performance console logging conform de modernste standaarden
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.TimestampFormat = "HH:mm:ss ";
                options.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger("STRIDE.Engine");
        logger.LogInformation("STRIDE Spatial Engine v1.0 opstarten (Native AOT Mode)...");

        // 3. Configureer Graceful Cancellation via Ctrl+C / SIGTERM
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            logger.LogWarning("Afbreek-signaal ontvangen (Ctrl+C). Pipeline wordt gecontroleerd leeggepompt...");
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            // 4. Laad omgevingsvariabelen en parse de YAML workflow (AST-safe via SecretResolver)
            string yamlContent = await File.ReadAllTextAsync(configPath, cts.Token);

            var envVars = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(k => (string)k.Key, v => (string)(v.Value ?? string.Empty), StringComparer.Ordinal);

            var loader = new WorkflowLoader(envVars);
            var config = loader.Load(yamlContent);

            logger.LogInformation("Workflow '{Name}' succesvol geladen.", config.Name);

            // 5. Start de verwerking over de DAG topologie
            var runner = new PipelineRunner(config, loggerFactory.CreateLogger<PipelineRunner>());
            await runner.ExecuteAsync();

            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Validatiefout"))
        {
            logger.LogError("Kritieke validatiefout: {Message}", ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "De pipeline is onverwacht gecrasht.");
            return 1;
        }
    }
}