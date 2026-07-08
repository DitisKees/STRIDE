using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformCentroid")]
public sealed class TransformCentroidBlock : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformCentroid requires a geometry field in the input schema.");
        }

        return schema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        await foreach (var transformed in BatchTransformUtilities.TransformBatchesAsync(
            context,
            reader,
            TransformBatch,
            cancellationToken).ConfigureAwait(false))
        {
            yield return transformed;
        }
    }

    private static RecordBatch TransformBatch(IRecordBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformCentroid requires a geometry field in the input schema.");
        }

        var geometries = batch.GeometryColumn(geometryOrdinal).Values;
        var transformed = new Geometry?[batch.RowCount];

        for (var row = 0; row < batch.RowCount; row++)
        {
            transformed[row] = geometries[row]?.Centroid;
        }

        var columns = BatchTransformUtilities.CopyColumns(batch);
        columns[geometryOrdinal] = new GeometryColumn(transformed);
        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }
}
