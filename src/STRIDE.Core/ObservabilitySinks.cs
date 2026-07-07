using STRIDE.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace STRIDE.Core;

internal sealed class ProgressMetricsSink : IBlockMetricsSink, IDisposable
{
    private readonly ConcurrentDictionary<string, NodeMetrics> _nodes = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public ProgressMetricsSink()
    {
        _timer = new Timer(static state => ((ProgressMetricsSink)state!).ReportProgress(), this, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void OnBatchIn(string nodeId, int rowCount, int bytes)
    {
        var metrics = _nodes.GetOrAdd(nodeId, static _ => new NodeMetrics());
        metrics.RecordBatchIn(rowCount, bytes);
    }

    public void OnBatchOut(string nodeId, int rowCount, int bytes)
    {
        var metrics = _nodes.GetOrAdd(nodeId, static _ => new NodeMetrics());
        metrics.RecordBatchOut(rowCount, bytes);
    }

    public void OnError(string nodeId)
    {
        var metrics = _nodes.GetOrAdd(nodeId, static _ => new NodeMetrics());
        metrics.RecordError();
    }

    public void Dispose()
    {
        _timer.Dispose();
        ReportProgress(finalReport: true);
    }

    private void ReportProgress(bool finalReport = false)
    {
        var elapsed = DateTimeOffset.UtcNow - _startedAt;
        var seconds = Math.Max(1, elapsed.TotalSeconds);

        foreach (var (nodeId, metrics) in _nodes.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var snapshot = metrics.Read();
            if (!finalReport && snapshot.RowsOut == 0 && snapshot.RowsIn == 0 && snapshot.Errors == 0)
            {
                continue;
            }

            var rowsPerSecond = snapshot.RowsOut / seconds;
            Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] node={nodeId} inRows={snapshot.RowsIn} outRows={snapshot.RowsOut} inBatches={snapshot.BatchesIn} outBatches={snapshot.BatchesOut} errors={snapshot.Errors} throughputRowsPerSec={rowsPerSecond:F2}{(finalReport ? " final=true" : string.Empty)}");
        }
    }

    private sealed class NodeMetrics
    {
        private long _batchesIn;
        private long _rowsIn;
        private long _bytesIn;
        private long _batchesOut;
        private long _rowsOut;
        private long _bytesOut;
        private long _errors;

        public void RecordBatchIn(int rowCount, int bytes)
        {
            Interlocked.Increment(ref _batchesIn);
            Interlocked.Add(ref _rowsIn, rowCount);
            Interlocked.Add(ref _bytesIn, bytes);
        }

        public void RecordBatchOut(int rowCount, int bytes)
        {
            Interlocked.Increment(ref _batchesOut);
            Interlocked.Add(ref _rowsOut, rowCount);
            Interlocked.Add(ref _bytesOut, bytes);
        }

        public void RecordError()
            => Interlocked.Increment(ref _errors);

        public NodeMetricsSnapshot Read()
            => new(
                BatchesIn: Volatile.Read(ref _batchesIn),
                RowsIn: Volatile.Read(ref _rowsIn),
                BytesIn: Volatile.Read(ref _bytesIn),
                BatchesOut: Volatile.Read(ref _batchesOut),
                RowsOut: Volatile.Read(ref _rowsOut),
                BytesOut: Volatile.Read(ref _bytesOut),
                Errors: Volatile.Read(ref _errors));
    }

    private readonly record struct NodeMetricsSnapshot(
        long BatchesIn,
        long RowsIn,
        long BytesIn,
        long BatchesOut,
        long RowsOut,
        long BytesOut,
        long Errors);
}

internal sealed class NdjsonErrorSink : IErrorSink, IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public NdjsonErrorSink(string path)
    {
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async ValueTask WriteRecordErrorAsync(string nodeId, string message, CancellationToken cancellationToken)
    {
        var entry = new ErrorEntry(
            DateTimeOffset.UtcNow,
            nodeId,
            message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct ErrorEntry(DateTimeOffset TimestampUtc, string NodeId, string Error);
}
