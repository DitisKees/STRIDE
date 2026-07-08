using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformBuffer")]
public sealed class TransformBufferBlock(double distance) : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformBuffer requires a geometry field in the input schema.");
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
            BufferBatch,
            cancellationToken).ConfigureAwait(false))
        {
            yield return transformed;
        }
    }

    private RecordBatch BufferBatch(IRecordBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformBuffer requires a geometry field in the input schema.");
        }

        var geometryValues = batch.GeometryColumn(geometryOrdinal).Values;
        var buffered = new Geometry?[batch.RowCount];
        for (var row = 0; row < batch.RowCount; row++)
        {
            buffered[row] = geometryValues[row] is Geometry geometry
                ? BufferOp.Buffer(geometry, distance)
                : null;
        }

        var columns = BatchTransformUtilities.CopyColumns(batch);
        columns[geometryOrdinal] = new GeometryColumn(buffered);

        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }
}
