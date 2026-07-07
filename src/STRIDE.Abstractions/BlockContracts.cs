using System.Threading.Channels;

namespace STRIDE.Abstractions;

public interface ISourceBlock
{
    Schema DeriveOutputSchema();

    IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, CancellationToken cancellationToken);
}

public interface ITransformBlock
{
    Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas);

    bool IsBlocking { get; }

    IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, CancellationToken cancellationToken);
}

public interface ISinkBlock
{
    ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken);
}

public interface ISpillManager
{
    ValueTask<ISpillScope> BeginScopeAsync(string blockId, CancellationToken cancellationToken);
}

public interface ISpillScope : IAsyncDisposable
{
    ValueTask<string> WritePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadPayloadsAsync(CancellationToken cancellationToken);
}

public interface IBlockMetricsSink
{
    void OnBatchIn(string nodeId, int rowCount, int bytes);

    void OnBatchOut(string nodeId, int rowCount, int bytes);

    void OnError(string nodeId);
}

public interface IErrorSink
{
    ValueTask WriteRecordErrorAsync(string nodeId, string message, CancellationToken cancellationToken);
}

public interface IBlockFactory
{
    IReadOnlySet<string> RegisteredTypes { get; }

    object Create(string type, BlockParams parameters);
}

public sealed record BlockContext(
    string NodeId,
    IReadOnlyDictionary<string, ChannelReader<IRecordBatch>> Inputs,
    IReadOnlyDictionary<string, ChannelWriter<IRecordBatch>> Outputs,
    ISpillManager SpillManager,
    IBlockMetricsSink Metrics,
    IErrorSink Errors,
    ErrorPolicy ErrorPolicy,
    BlockParams Parameters,
    CancellationToken CancellationToken);