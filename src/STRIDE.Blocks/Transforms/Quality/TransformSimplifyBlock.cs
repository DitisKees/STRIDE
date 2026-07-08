using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformSimplify")]
public sealed class TransformSimplifyBlock(double tolerance, bool preserveTopology = true) : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        await foreach (var transformedBatch in BatchTransformUtilities.TransformBatchesAsync(
            context,
            reader,
            TransformBatch,
            cancellationToken).ConfigureAwait(false))
        {
            yield return transformedBatch;
        }
    }

    private RecordBatch TransformBatch(IRecordBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformSimplify requires a geometry column.");
        }

        var transformed = new Geometry?[batch.RowCount];
        var source = batch.GeometryColumn(geometryOrdinal).Values;
        for (var row = 0; row < batch.RowCount; row++)
        {
            var geometry = source[row];
            if (geometry is null)
            {
                continue;
            }

            transformed[row] = preserveTopology
                ? TopologyPreservingSimplifier.Simplify(geometry, tolerance)
                : DouglasPeuckerSimplifier.Simplify(geometry, tolerance);
        }

        var columns = BatchTransformUtilities.CopyColumns(batch);
        columns[geometryOrdinal] = new GeometryColumn(transformed);
        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }
}
