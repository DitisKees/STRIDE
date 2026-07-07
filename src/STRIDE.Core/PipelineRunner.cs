using STRIDE.Abstractions;
using STRIDE.Schema;
using System.Threading.Channels;

namespace STRIDE.Core;

public sealed class PipelineRunner
{
    private readonly DagValidator _validator = new();

    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public async Task<int> RunAsync(
        WorkflowDefinition workflow,
        IBlockFactory blockFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(blockFactory);

        var validation = _validator.Validate(workflow, blockFactory);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var spillManager = new SpillManager(workflow.Settings.SpillDirectory);
        using var metrics = new ProgressMetricsSink();
        await using var errors = new NdjsonErrorSink(workflow.Settings.ErrorLog);

        var inputsByNode = new Dictionary<string, Dictionary<string, ChannelReader<IRecordBatch>>>(StringComparer.Ordinal);
        var outputsByNode = new Dictionary<string, Dictionary<string, ChannelWriter<IRecordBatch>>>(StringComparer.Ordinal);

        foreach (var nodeId in validation.NodeById.Keys)
        {
            inputsByNode[nodeId] = new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase);
            outputsByNode[nodeId] = new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase);
        }

        var outputPortWriters = new Dictionary<(string NodeId, string Port), List<ChannelWriter<IRecordBatch>>>();

        foreach (var node in validation.NodeById.Values)
        {
            foreach (var input in validation.Inputs[node.Id])
            {
                var channel = Channel.CreateBounded<IRecordBatch>(new BoundedChannelOptions(workflow.Settings.BatchSize)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false,
                });

                inputsByNode[node.Id][input.Key] = channel.Reader;

                var key = (input.Value.UpstreamNodeId, input.Value.UpstreamPort);
                if (!outputPortWriters.TryGetValue(key, out var writers))
                {
                    writers = [];
                    outputPortWriters[key] = writers;
                }

                writers.Add(channel.Writer);
            }
        }

        foreach (var entry in outputPortWriters)
        {
            outputsByNode[entry.Key.NodeId][entry.Key.Port] =
                entry.Value.Count == 1
                    ? entry.Value[0]
                    : new BroadcastChannelWriter(entry.Value);
        }

        var blockInstances = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var node in validation.NodeById.Values)
        {
            blockInstances[node.Id] = blockFactory.Create(node.Type, BlockParams.FromStringMap(node.Params));
        }

        var tasks = new List<Task>(validation.NodeById.Count);
        foreach (var nodeId in validation.TopologicalOrder)
        {
            var node = validation.NodeById[nodeId];
            var meteredInputs = inputsByNode[node.Id].ToDictionary(
                static kvp => kvp.Key,
                kvp => (ChannelReader<IRecordBatch>)new MeteredChannelReader(node.Id, kvp.Value, metrics),
                StringComparer.OrdinalIgnoreCase);

            var meteredOutputs = outputsByNode[node.Id].ToDictionary(
                static kvp => kvp.Key,
                kvp => (ChannelWriter<IRecordBatch>)new MeteredChannelWriter(node.Id, kvp.Key, kvp.Value, metrics),
                StringComparer.OrdinalIgnoreCase);

            var context = new BlockContext(
                node.Id,
                meteredInputs,
                meteredOutputs,
                spillManager,
                metrics,
                errors,
                node.ErrorPolicy,
                BlockParams.FromStringMap(node.Params),
                linkedCts.Token);

            var instance = blockInstances[nodeId];
            tasks.Add(instance switch
            {
                ISourceBlock source => RunSourceAsync(source, context, linkedCts),
                ITransformBlock transform => RunTransformAsync(transform, context, linkedCts),
                ISinkBlock sink => RunSinkAsync(sink, context, linkedCts),
                _ => Task.FromException(new InvalidOperationException($"Node '{node.Id}' has unsupported block type '{node.Type}'.")),
            });
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            linkedCts.Cancel();
            throw;
        }
    }

    private static async Task RunSourceAsync(
        ISourceBlock source,
        BlockContext context,
        CancellationTokenSource cancellationTokenSource)
    {
        Exception? completionException = null;

        try
        {
            await foreach (var batch in source.ExecuteAsync(context, context.CancellationToken).ConfigureAwait(false))
            {
                await WriteDefaultOutputAsync(context, batch, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            completionException = HandleBlockException(ex, context, cancellationTokenSource);
            if (completionException is not null)
            {
                throw completionException;
            }
        }
        finally
        {
            CompleteOutputs(context, completionException);
        }
    }

    private static async Task RunTransformAsync(
        ITransformBlock transform,
        BlockContext context,
        CancellationTokenSource cancellationTokenSource)
    {
        Exception? completionException = null;

        try
        {
            await foreach (var batch in transform.ExecuteAsync(context, context.CancellationToken).ConfigureAwait(false))
            {
                await WriteDefaultOutputAsync(context, batch, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            completionException = HandleBlockException(ex, context, cancellationTokenSource);
            if (completionException is not null)
            {
                throw completionException;
            }
        }
        finally
        {
            CompleteOutputs(context, completionException);
        }
    }

    private static async Task RunSinkAsync(
        ISinkBlock sink,
        BlockContext context,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await sink.ExecuteAsync(context, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            var completionException = HandleBlockException(ex, context, cancellationTokenSource);
            if (completionException is not null)
            {
                throw completionException;
            }
        }
    }

    private static Exception? HandleBlockException(
        Exception ex,
        BlockContext context,
        CancellationTokenSource cancellationTokenSource)
    {
        context.Metrics.OnError(context.NodeId);

        if (context.ErrorPolicy == ErrorPolicy.Ignore)
        {
            context.Errors.WriteRecordErrorAsync(context.NodeId, ex.Message, context.CancellationToken).GetAwaiter().GetResult();
            return null;
        }

        if (context.ErrorPolicy == ErrorPolicy.StopBranch)
        {
            return null;
        }

        cancellationTokenSource.Cancel();
        return ex;
    }

    private static async ValueTask WriteDefaultOutputAsync(
        BlockContext context,
        IRecordBatch batch,
        CancellationToken cancellationToken)
    {
        if (!context.Outputs.TryGetValue("out", out var output))
        {
            return;
        }

        await output.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private static void CompleteOutputs(BlockContext context, Exception? completionException)
    {
        foreach (var output in context.Outputs.Values)
        {
            output.TryComplete(completionException);
        }
    }
}

internal sealed class BroadcastChannelWriter : ChannelWriter<IRecordBatch>
{
    private readonly IReadOnlyList<ChannelWriter<IRecordBatch>> _writers;

    public BroadcastChannelWriter(IReadOnlyList<ChannelWriter<IRecordBatch>> writers)
    {
        _writers = writers;
    }

    public override bool TryComplete(Exception? error = null)
    {
        var success = true;
        foreach (var writer in _writers)
        {
            success &= writer.TryComplete(error);
        }

        return success;
    }

    public override bool TryWrite(IRecordBatch item)
    {
        var success = true;
        foreach (var writer in _writers)
        {
            success &= writer.TryWrite(item);
        }

        return success;
    }

    public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var writer in _writers)
        {
            if (!await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    public override ValueTask WriteAsync(IRecordBatch item, CancellationToken cancellationToken = default)
        => new(Task.WhenAll(_writers.Select(w => w.WriteAsync(item, cancellationToken).AsTask())));
}

internal sealed class MeteredChannelReader : ChannelReader<IRecordBatch>
{
    private readonly string _nodeId;
    private readonly ChannelReader<IRecordBatch> _inner;
    private readonly IBlockMetricsSink _metrics;

    public MeteredChannelReader(string nodeId, ChannelReader<IRecordBatch> inner, IBlockMetricsSink metrics)
    {
        _nodeId = nodeId;
        _inner = inner;
        _metrics = metrics;
    }

    public override Task Completion => _inner.Completion;

    public override bool TryPeek(out IRecordBatch item)
    {
        if (_inner.TryPeek(out var candidate))
        {
            item = candidate;
            return true;
        }

        item = null!;
        return false;
    }

    public override bool TryRead(out IRecordBatch item)
    {
        if (_inner.TryRead(out var candidate))
        {
            item = candidate;
            _metrics.OnBatchIn(_nodeId, item.RowCount, 0);
            return true;
        }

        item = null!;
        return false;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        => _inner.WaitToReadAsync(cancellationToken);
}

internal sealed class MeteredChannelWriter : ChannelWriter<IRecordBatch>
{
    private readonly string _nodeId;
    private readonly string _port;
    private readonly ChannelWriter<IRecordBatch> _inner;
    private readonly IBlockMetricsSink _metrics;

    public MeteredChannelWriter(string nodeId, string port, ChannelWriter<IRecordBatch> inner, IBlockMetricsSink metrics)
    {
        _nodeId = nodeId;
        _port = port;
        _inner = inner;
        _metrics = metrics;
    }

    public override bool TryComplete(Exception? error = null)
        => _inner.TryComplete(error);

    public override bool TryWrite(IRecordBatch item)
    {
        if (_inner.TryWrite(item))
        {
            Track(item);
            return true;
        }

        return false;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => _inner.WaitToWriteAsync(cancellationToken);

    public override async ValueTask WriteAsync(IRecordBatch item, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Track(item);
    }

    private void Track(IRecordBatch batch)
    {
        _metrics.OnBatchOut(_nodeId, batch.RowCount, 0);
    }
}