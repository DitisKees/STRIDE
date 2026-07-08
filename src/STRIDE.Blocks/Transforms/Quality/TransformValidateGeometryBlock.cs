using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;
using STRIDE.Abstractions;
using System.Runtime.InteropServices;

namespace STRIDE.Blocks;

[StrideBlock("TransformValidateGeometry")]
public sealed class TransformValidateGeometryBlock(bool dropInvalid = true) : ITransformBlock
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
                    throw new InvalidOperationException("TransformValidateGeometry requires a geometry column.");
                }

                var rows = new List<int>(batch.RowCount);
                var geometries = batch.GeometryColumn(geometryOrdinal).Values;
                for (var row = 0; row < batch.RowCount; row++)
                {
                    var geometry = geometries[row];
                    if (geometry is null)
                    {
                        rows.Add(row);
                        continue;
                    }

                    var isValid = new IsValidOp(geometry).IsValid;
                    if (isValid)
                    {
                        rows.Add(row);
                        continue;
                    }

                    if (dropInvalid)
                    {
                        await context.Errors.WriteRecordErrorAsync(context.NodeId, "Invalid geometry skipped.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    rows.Add(row);
                }

                if (rows.Count == 0)
                {
                    continue;
                }

                if (batch is RecordBatch recordBatch)
                {
                    yield return recordBatch.SelectRows(CollectionsMarshal.AsSpan(rows));
                }
                else
                {
                    var sliced = BatchTransformUtilities.EnsureRecordBatch(batch).SelectRows(CollectionsMarshal.AsSpan(rows));
                    yield return sliced;
                }
            }
        }
    }
}
