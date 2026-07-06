using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using STRIDE.Abstractions;
using STRIDE.Blocks.Models;

namespace STRIDE.Blocks;

[StrideBlock("TransformBuffer")]
public sealed class TransformBuffer : ITransformBlock
{
    private readonly double _distance;
    public bool IsBlocking => false;

    public TransformBuffer(Dictionary<string, string> parameters)
    {
        _distance = parameters.TryGetValue("distance", out string? distStr) && double.TryParse(distStr, out double d) ? d : 0.0;
    }

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas) => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
            IReadOnlyDictionary<string, IAsyncEnumerable<IRecordBatch>> inputs,
            BlockContext ctx,
            [EnumeratorCancellation] CancellationToken ct)
    {
        // Vang annulering direct op de invoerstroom op
        int totalRows = 0;
        await foreach (var batch in inputs["in"].WithCancellation(ct))
        {
            int geoOrdinal = batch.Schema.GeometryFieldIndex;
            if (geoOrdinal == -1)
            {
                yield return batch;
                continue;
            }

            var geoColumn = batch.GetGeometryColumn(geoOrdinal);
            var transformedGeometries = System.Buffers.ArrayPool<Geometry>.Shared.Rent(batch.RowCount);

            // Klassieke sequentiële lus: dit mag wél met ref structs omdat er geen closures zijn
            for (int i = 0; i < batch.RowCount; i++)
            {
                var originalGeo = geoColumn.Geometries[i];
                transformedGeometries[i] = originalGeo != null ? BufferOp.Buffer(originalGeo, _distance) : null!;
            }

            var outputBatch = new GeneratedRecordBatch(batch.Schema, batch.RowCount);

            if (batch is GeneratedRecordBatch genBatch)
            {
                genBatch.ShareColumnsWith(outputBatch);
            }
            else
            {
                throw new NotSupportedException("Only GeneratedRecordBatch is supported for structural sharing.");
            }

            outputBatch.AddGeometryColumn(geoOrdinal, transformedGeometries, batch.RowCount);

            totalRows += batch.RowCount;
            Console.WriteLine($"[TransformBuffer] Processed {totalRows} rows total.");

            batch.Dispose();
            yield return outputBatch;
        }
    }
}