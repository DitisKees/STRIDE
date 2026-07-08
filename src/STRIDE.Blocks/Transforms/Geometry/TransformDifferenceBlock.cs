using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformDifference")]
public sealed class TransformDifferenceBlock : ITransformBlock
{
    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var inputReader) || !context.Inputs.TryGetValue("subtract", out var subtractReader))
        {
            yield break;
        }

        var subtractGeometries = new List<Geometry>();
        while (await subtractReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (subtractReader.TryRead(out var batch))
            {
                var ordinal = batch.Schema.GeometryFieldIndex;
                if (ordinal < 0)
                {
                    continue;
                }

                subtractGeometries.AddRange(batch.GeometryColumn(ordinal).Values.Where(static g => g is not null)!.Cast<Geometry>());
            }
        }

        var subtraction = subtractGeometries.Count == 0 ? null : UnaryUnionOp.Union(subtractGeometries);

        while (await inputReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (inputReader.TryRead(out var batch))
            {
                var ordinal = batch.Schema.GeometryFieldIndex;
                if (ordinal < 0)
                {
                    throw new InvalidOperationException("TransformDifference requires geometry in the input stream.");
                }

                var geometries = batch.GeometryColumn(ordinal).Values;
                var output = new Geometry?[batch.RowCount];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    output[row] = subtraction is null || geometries[row] is null
                        ? geometries[row]
                        : geometries[row]!.Difference(subtraction);
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[ordinal] = new GeometryColumn(output);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }
}
