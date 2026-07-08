using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformAffine")]
public sealed class TransformAffineBlock(
    double m00 = 1,
    double m01 = 0,
    double m02 = 0,
    double m10 = 0,
    double m11 = 1,
    double m12 = 0) : ITransformBlock
{
    private readonly AffineTransformation _affine = new(m00, m01, m02, m10, m11, m12);

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
                    throw new InvalidOperationException("TransformAffine requires a geometry column.");
                }

                var transformed = new Geometry?[batch.RowCount];
                var source = batch.GeometryColumn(geometryOrdinal).Values;
                for (var row = 0; row < batch.RowCount; row++)
                {
                    transformed[row] = source[row] is Geometry geometry
                        ? _affine.Transform(geometry)
                        : null;
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[geometryOrdinal] = new GeometryColumn(transformed);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
