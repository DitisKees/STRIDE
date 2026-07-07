using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformRemoveDuplicateVertices")]
public sealed class TransformRemoveDuplicateVerticesBlock : ITransformBlock
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
                var geomOrdinal = batch.Schema.GeometryFieldIndex;
                if (geomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformRemoveDuplicateVertices requires a geometry field.");
                }

                var source = batch.GeometryColumn(geomOrdinal).Values;
                var transformed = new Geometry?[batch.RowCount];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    transformed[row] = RemoveDuplicateVertices(source[row]);
                }

                var columns = BatchTransformUtilities.CopyColumns(batch);
                columns[geomOrdinal] = new GeometryColumn(transformed);
                yield return new RecordBatch(batch.Schema, batch.RowCount, columns);
            }
        }
    }

    private static Geometry? RemoveDuplicateVertices(Geometry? geometry)
    {
        if (geometry is null)
        {
            return null;
        }

        if (geometry is LineString line)
        {
            return line.Factory.CreateLineString(Deduplicate(line.Coordinates));
        }

        if (geometry is Polygon polygon)
        {
            var shell = polygon.Factory.CreateLinearRing(Deduplicate(polygon.ExteriorRing.Coordinates));
            var holes = new LinearRing[polygon.NumInteriorRings];
            for (var i = 0; i < holes.Length; i++)
            {
                holes[i] = polygon.Factory.CreateLinearRing(Deduplicate(polygon.GetInteriorRingN(i).Coordinates));
            }

            return polygon.Factory.CreatePolygon(shell, holes);
        }

        return geometry;
    }

    private static Coordinate[] Deduplicate(Coordinate[] coordinates)
    {
        if (coordinates.Length <= 1)
        {
            return coordinates;
        }

        var output = new List<Coordinate>(coordinates.Length) { coordinates[0] };
        for (var i = 1; i < coordinates.Length; i++)
        {
            if (!coordinates[i].Equals2D(output[^1]))
            {
                output.Add(coordinates[i]);
            }
        }

        return output.ToArray();
    }
}
