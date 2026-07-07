using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay.Snap;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformSnapGeometries")]
public sealed class TransformSnapGeometriesBlock(double tolerance = 0.001) : ITransformBlock
{
    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var inputReader) || !context.Inputs.TryGetValue("reference", out var referenceReader))
        {
            yield break;
        }

        var reference = new List<Geometry>();
        while (await referenceReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (referenceReader.TryRead(out var batch))
            {
                var ordinal = batch.Schema.GeometryFieldIndex;
                if (ordinal < 0)
                {
                    continue;
                }

                reference.AddRange(batch.GeometryColumn(ordinal).Values.Where(static g => g is not null)!.Cast<Geometry>());
            }
        }

        while (await inputReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (inputReader.TryRead(out var batch))
            {
                var ordinal = batch.Schema.GeometryFieldIndex;
                if (ordinal < 0)
                {
                    throw new InvalidOperationException("TransformSnapGeometries requires geometry on input stream.");
                }

                var transformed = new Geometry?[batch.RowCount];
                var source = batch.GeometryColumn(ordinal).Values;
                for (var row = 0; row < batch.RowCount; row++)
                {
                    if (source[row] is not Geometry geometry || reference.Count == 0)
                    {
                        transformed[row] = source[row];
                        continue;
                    }

                    var snapped = geometry;
                    foreach (var referenceGeometry in reference)
                    {
                        var pair = GeometrySnapper.Snap(snapped, referenceGeometry, tolerance);
                        snapped = pair[0];
                    }

                    transformed[row] = snapped;
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[ordinal] = new GeometryColumn(transformed);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
