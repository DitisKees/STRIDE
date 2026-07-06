using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using STRIDE.Blocks.Models;

namespace STRIDE.Blocks;

[StrideBlock("TransformCreateGeometry")]
public sealed class TransformCreateGeometry : ITransformBlock
{
    private readonly string _xColumnName;
    private readonly string _yColumnName;
    private readonly GeometryFactory _geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public bool IsBlocking => false;

    public TransformCreateGeometry(Dictionary<string, string> parameters)
    {
        _xColumnName = parameters.TryGetValue("xColumn", out string? x) ? x : "x";
        _yColumnName = parameters.TryGetValue("yColumn", out string? y) ? y : "y";
    }

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var inputSchema = inputSchemas["in"];
        var updatedFields = inputSchema.Fields.ToBuilder();
        updatedFields.Add(new FieldDef("geometry", FieldType.Geometry, Nullable: false));
        return new Schema(updatedFields.ToImmutable());
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        IReadOnlyDictionary<string, IAsyncEnumerable<IRecordBatch>> inputs,
        BlockContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int totalRows = 0;
        await foreach (var batch in inputs["in"].WithCancellation(ct))
        {
            if (!batch.Schema.TryGetOrdinal(_xColumnName, out int xOrdinal) ||
                !batch.Schema.TryGetOrdinal(_yColumnName, out int yOrdinal))
            {
                ctx.ErrorLogger($"Kolommen '{_xColumnName}' of '{_yColumnName}' niet gevonden.", new KeyNotFoundException());
                if (ctx.ConfiguredErrorPolicy == ErrorPolicy.StopPipeline) throw new InvalidOperationException();
                continue;
            }

            var xData = batch.GetColumnMemory<double>(xOrdinal).Span;
            var yData = batch.GetColumnMemory<double>(yOrdinal).Span;

            var points = System.Buffers.ArrayPool<Geometry>.Shared.Rent(batch.RowCount);
            for (int i = 0; i < batch.RowCount; i++)
            {
                points[i] = _geometryFactory.CreatePoint(new Coordinate(xData[i], yData[i]));
            }

            var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema> { { "in", batch.Schema } });
            var outputBatch = new GeneratedRecordBatch(outputSchema, batch.RowCount);

            if (batch is GeneratedRecordBatch genBatch)
            {
                genBatch.ShareColumnsWith(outputBatch);
            }
            else
            {
                throw new NotSupportedException("Only GeneratedRecordBatch is supported for structural sharing.");
            }

            outputSchema.TryGetOrdinal("geometry", out int geomOrdinal);
            outputBatch.AddGeometryColumn(geomOrdinal, points, batch.RowCount);

            batch.Dispose();
            totalRows += outputBatch.RowCount;
            Console.WriteLine($"[TransformCreateGeometry] Processed {totalRows} rows total.");
            yield return outputBatch;
        }
    }
}