namespace STRIDE.Abstractions;

public interface IBlock
{
    Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas);
}

public interface ISourceBlock : IBlock
{
    IAsyncEnumerable<IRecordBatch> StreamAsync(BlockContext ctx, CancellationToken ct);
}

public interface ITransformBlock : IBlock
{
    bool IsBlocking { get; }
    IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        IReadOnlyDictionary<string, IAsyncEnumerable<IRecordBatch>> inputs,
        BlockContext ctx,
        CancellationToken ct);
}

public interface ISinkBlock : IBlock
{
    Task WriteAsync(IAsyncEnumerable<IRecordBatch> input, BlockContext ctx, CancellationToken ct);
}