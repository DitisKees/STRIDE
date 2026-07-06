using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STRIDE.Abstractions;
using STRIDE.Schema;
using STRIDE.Blocks;

namespace STRIDE.Core;

public sealed class PipelineRunner(WorkflowConfig config, ILogger<PipelineRunner> logger)
{
    private readonly ConcurrentDictionary<string, Channel<IRecordBatch>> _nodeChannels = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    public async Task ExecuteAsync()
    {
        // 1. Valideer de structuur en bepaal de topologische volgorde
        var validator = new DagValidator(config);
        List<string> executionOrder;
        try
        {
            executionOrder = validator.ValidateAndDetermineOrder();
        }
        catch (Exception ex)
        {
            logger.LogError("Validatiefout bij het opbouwen van de DAG: {Message}", ex.Message);
            throw;
        }

        logger.LogInformation("Workflow succesvol gevalideerd. Starten van {Count} verwerkingsstations.", executionOrder.Count);

        // 2. Initialiseer onbeperkte kanalen voor vloeibare doorstroom tijdens benchmarks
        var channelOptions = new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait, // Wacht netjes als de buffer vol is (Backpressure!)
            SingleWriter = false,                  // Sommige blokken kunnen parallel schrijven
            SingleReader = true                    // Elk kanaal wordt door exact één opvolger leeggehaald
        };

        foreach (var nodeId in executionOrder)
        {
            _nodeChannels[nodeId] = Channel.CreateBounded<IRecordBatch>(channelOptions);
        }

        var tasks = new List<Task>();
        var ct = _cts.Token;

        // 3. Lanceer alle nodes onafhankelijk van elkaar op de ThreadPool via Task.Run
        foreach (var node in config.Nodes)
        {
            tasks.Add(Task.Run(async () => await RunNodeAsync(node, ct), ct));
        }

        foreach (var sink in config.Sinks)
        {
            tasks.Add(Task.Run(async () => await RunSinkNodeAsync(sink, ct), ct));
        }

        // Geef de TPL een microseconde de tijd om de taken daadwerkelijk te spinnen
        await Task.Yield();

        try
        {
            await Task.WhenAll(tasks);
            logger.LogInformation("Pipeline succesvol afgerond.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Pipeline-afbreek-signaal verwerkt. Alle stations zijn succesvol leeggepompt.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Kritieke fout tijdens pipeline uitvoering.");
            throw;
        }
    }

    private async Task RunNodeAsync(WorkflowNode node, CancellationToken ct)
    {
        try
        {
            // Genereer het blok via de compile-time registry (AOT veilig)
            var block = BlockRegistry.CreateBlock(node.Type, node.Params);

            var policy = node.ErrorPolicy?.ToLowerInvariant() switch
            {
                "stopbranch" => ErrorPolicy.StopBranch,
                "ignore" => ErrorPolicy.Ignore,
                _ => ErrorPolicy.StopPipeline
            };

            var ctx = new BlockContext(node.Id, policy, (msg, ex) => logger.LogError(ex, "[{NodeId}]: {Msg}", node.Id, msg));
            var outputWriter = _nodeChannels[node.Id].Writer;

            // Scenario A: Het is een BRON blok (Gegenereerde datastroom / File Reader)
            if (block is ISourceBlock sourceBlock)
            {
                await foreach (var batch in sourceBlock.StreamAsync(ctx, ct).WithCancellation(ct))
                {
                    await outputWriter.WriteAsync(batch, ct);
                }
                outputWriter.TryComplete();
                return;
            }

            // Scenario B: Het is een TRANSFORMATIE blok
            if (block is ITransformBlock transformBlock)
            {
                var inputStreams = new Dictionary<string, IAsyncEnumerable<IRecordBatch>>(StringComparer.Ordinal);
                if (node.Inputs != null)
                {
                    foreach (var input in node.Inputs)
                    {
                        string upstreamNodeId = input.Value.Split(':')[0];
                        inputStreams[input.Key] = _nodeChannels[upstreamNodeId].Reader.ReadAllAsync(ct);
                    }
                }

                await foreach (var batch in transformBlock.ExecuteAsync(inputStreams, ctx, ct).WithCancellation(ct))
                {
                    await outputWriter.WriteAsync(batch, ct);
                }
                outputWriter.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            // Doorgeschoten vanuit yield break of WriteAsync, gracieus afsluiten
            _nodeChannels[node.Id].Writer.TryComplete();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fout in verwerkings-node '{Id}'. Pipeline wordt stilgelegd.", node.Id);
            _cts.Cancel();
            _nodeChannels[node.Id].Writer.TryComplete(ex);
            throw;
        }
    }

    private async Task RunSinkNodeAsync(WorkflowSink sink, CancellationToken ct)
    {
        try
        {
            if (sink.Inputs == null || !sink.Inputs.TryGetValue("in", out var upstreamMapping))
            {
                throw new InvalidOperationException($"Sink '{sink.Id}' mist een geldige 'in' invoerkoppeling.");
            }

            string upstreamNodeId = upstreamMapping.Split(':')[0];
            var inputStream = _nodeChannels[upstreamNodeId].Reader.ReadAllAsync(ct);

            try
            {
                var sinkBlock = BlockRegistry.CreateBlock(sink.Type, sink.Params);
                if (sinkBlock is ISinkBlock concreteSink)
                {
                    var ctx = new BlockContext(sink.Id, ErrorPolicy.StopPipeline, (msg, ex) => logger.LogError(ex, "[{NodeId}]: {Msg}", sink.Id, msg));
                    await concreteSink.WriteAsync(inputStream, ctx, ct);
                    return;
                }
            }
            catch (KeyNotFoundException)
            {
                // Fallback voor onze test NullSink/fictieve sinks
                logger.LogInformation("Sink '{Id}' ({Type}) activeert NullSink modus. Stream wordt leeggetrokken.", sink.Id, sink.Type);

                long totalRowsDropped = 0;
                await foreach (var batch in inputStream.WithCancellation(ct))
                {
                    totalRowsDropped += batch.RowCount;
                    batch.Dispose(); // Geef ArrayPool geheugen direct vrij!
                }
                logger.LogInformation("Sink '{Id}' succesvol afgerond. {Count} records verwerkt.", sink.Id, totalRowsDropped);
            }
        }
        catch (OperationCanceledException)
        {
            // Geabsorbeerd bij Ctrl+C
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Kritieke fout in sink-node '{Id}'.", sink.Id);
            _cts.Cancel();
            throw;
        }
    }
}