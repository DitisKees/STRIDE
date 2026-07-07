using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformEnvelope")]
public sealed class TransformEnvelopeBlock : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformEnvelope requires a geometry field in the input schema.");
        }

        return schema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                var geometryOrdinal = batch.Schema.GeometryFieldIndex;
                var geometries = batch.GeometryColumn(geometryOrdinal).Values;
                var transformed = new Geometry?[batch.RowCount];

                for (var row = 0; row < batch.RowCount; row++)
                {
                    transformed[row] = geometries[row]?.Envelope;
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[geometryOrdinal] = new GeometryColumn(transformed);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
