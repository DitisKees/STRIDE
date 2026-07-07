using NetTopologySuite.Densify;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformDensify")]
public sealed class TransformDensifyBlock(double distanceTolerance) : ITransformBlock
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

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                var geometryOrdinal = batch.Schema.GeometryFieldIndex;
                if (geometryOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformDensify requires a geometry column.");
                }

                var transformed = new Geometry?[batch.RowCount];
                var source = batch.GeometryColumn(geometryOrdinal).Values;
                for (var row = 0; row < batch.RowCount; row++)
                {
                    transformed[row] = source[row] is Geometry geometry
                        ? Densifier.Densify(geometry, distanceTolerance)
                        : null;
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[geometryOrdinal] = new GeometryColumn(transformed);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
