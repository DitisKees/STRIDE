using STRIDE.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.LinearReferencing;
using NetTopologySuite.Operation.Polygonize;

namespace STRIDE.Blocks;

[StrideBlock("TransformSplitGeometry")]
public sealed class TransformSplitGeometryBlock(string? splitterWkt = null, string? splitterField = null) : ITransformBlock
{
    private readonly Geometry? _staticSplitter = string.IsNullOrWhiteSpace(splitterWkt)
        ? null
        : new WKTReader().Read(splitterWkt);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformSplitGeometry requires a geometry field in the input schema.");
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
                if (batch.RowCount == 0)
                {
                    continue;
                }

                var geometryOrdinal = batch.Schema.GeometryFieldIndex;
                if (geometryOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformSplitGeometry requires a geometry field in the input schema.");
                }

                var splitterOrdinal = ResolveSplitterOrdinal(batch.Schema);
                var outputRows = new List<string[]>(batch.RowCount);
                var outputGeometries = new List<Geometry?>(batch.RowCount * 2);

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var geometry = batch.GeometryColumn(geometryOrdinal).Values[row];
                    if (geometry is null)
                    {
                        AppendOutputRow(batch, row, null, outputRows, outputGeometries);
                        continue;
                    }

                    var splitter = ResolveSplitterGeometry(batch, splitterOrdinal, row, geometry.Factory);
                    if (splitter is null || splitter.IsEmpty)
                    {
                        AppendOutputRow(batch, row, geometry, outputRows, outputGeometries);
                        continue;
                    }

                    var parts = SplitGeometry(geometry, splitter);
                    if (parts.Count == 0)
                    {
                        AppendOutputRow(batch, row, geometry, outputRows, outputGeometries);
                        continue;
                    }

                    foreach (var part in parts)
                    {
                        AppendOutputRow(batch, row, part, outputRows, outputGeometries);
                    }
                }

                if (outputRows.Count == 0)
                {
                    continue;
                }

                var projected = RecordBatch.FromRows(batch.Schema, outputRows);
                var columns = new object?[batch.Schema.Fields.Length];
                for (var col = 0; col < batch.Schema.Fields.Length; col++)
                {
                    columns[col] = col == geometryOrdinal
                        ? new GeometryColumn(outputGeometries.ToArray())
                        : BatchTransformUtilities.CopyColumn(projected, col);
                }

                yield return new RecordBatch(batch.Schema, outputRows.Count, columns);
            }
        }
    }

    private int ResolveSplitterOrdinal(Schema schema)
    {
        if (string.IsNullOrWhiteSpace(splitterField))
        {
            return -1;
        }

        if (!schema.TryGetOrdinal(splitterField, out var ordinal))
        {
            throw new InvalidOperationException($"TransformSplitGeometry splitter field '{splitterField}' was not found in the input schema.");
        }

        return ordinal;
    }

    private Geometry? ResolveSplitterGeometry(IRecordBatch batch, int splitterOrdinal, int row, GeometryFactory geometryFactory)
    {
        if (_staticSplitter is not null)
        {
            return _staticSplitter;
        }

        if (splitterOrdinal < 0)
        {
            return null;
        }

        if (batch.Schema.Fields[splitterOrdinal].Type == FieldType.Geometry)
        {
            return batch.GeometryColumn(splitterOrdinal).Values[row];
        }

        var raw = batch.GetValueAsString(splitterOrdinal, row);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = new WKTReader().Read(raw);
        parsed.SRID = geometryFactory.SRID;
        return geometryFactory.CreateGeometry(parsed);
    }

    private static void AppendOutputRow(
        IRecordBatch batch,
        int sourceRow,
        Geometry? geometry,
        List<string[]> outputRows,
        List<Geometry?> outputGeometries)
    {
        var values = new string[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            values[col] = batch.GetValueAsString(col, sourceRow);
        }

        outputRows.Add(values);
        outputGeometries.Add(geometry);
    }

    private static IReadOnlyList<Geometry> SplitGeometry(Geometry input, Geometry splitter)
    {
        if (input.IsEmpty || splitter.IsEmpty || !input.EnvelopeInternal.Intersects(splitter.EnvelopeInternal))
        {
            return [input];
        }

        return input switch
        {
            Polygon polygon => SplitPolygon(polygon, splitter),
            MultiPolygon multiPolygon => SplitMultiPolygon(multiPolygon, splitter),
            LineString lineString => SplitLineString(lineString, splitter),
            MultiLineString multiLineString => SplitMultiLineString(multiLineString, splitter),
            _ => [input],
        };
    }

    private static IReadOnlyList<Geometry> SplitPolygon(Polygon polygon, Geometry splitter)
    {
        var polygonizer = new Polygonizer();
        var noded = polygon.Boundary.Union(splitter);
        polygonizer.Add(noded);

        var pieces = polygonizer.GetPolygons()
            .OfType<Polygon>()
            .Where(piece => piece.Area > 0 && polygon.Covers(piece.PointOnSurface))
            .Cast<Geometry>()
            .ToArray();

        return pieces.Length > 0 ? pieces : [polygon];
    }

    private static IReadOnlyList<Geometry> SplitMultiPolygon(MultiPolygon multiPolygon, Geometry splitter)
    {
        var parts = new List<Geometry>();
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            if (multiPolygon.GetGeometryN(i) is Polygon polygon)
            {
                parts.AddRange(SplitPolygon(polygon, splitter));
            }
        }

        return parts.Count > 0 ? parts : [multiPolygon];
    }

    private static IReadOnlyList<Geometry> SplitLineString(LineString line, Geometry splitter)
    {
        var intersections = CollectSplitCoordinates(line.Intersection(splitter));
        if (intersections.Count == 0)
        {
            return [line];
        }

        var indexedLine = new LengthIndexedLine(line);
        var splitIndexes = new List<double>(intersections.Count + 2)
        {
            indexedLine.StartIndex,
            indexedLine.EndIndex,
        };

        foreach (var coordinate in intersections)
        {
            var projected = indexedLine.Project(coordinate);
            if (projected > indexedLine.StartIndex && projected < indexedLine.EndIndex)
            {
                splitIndexes.Add(projected);
            }
        }

        splitIndexes.Sort();

        const double epsilon = 1e-9;
        var parts = new List<Geometry>();
        for (var i = 1; i < splitIndexes.Count; i++)
        {
            var start = splitIndexes[i - 1];
            var end = splitIndexes[i];
            if (end - start <= epsilon)
            {
                continue;
            }

            var segment = indexedLine.ExtractLine(start, end);
            if (segment is LineString lineSegment && !lineSegment.IsEmpty && lineSegment.Length > epsilon)
            {
                parts.Add(lineSegment);
            }
        }

        return parts.Count > 0 ? parts : [line];
    }

    private static IReadOnlyList<Geometry> SplitMultiLineString(MultiLineString multiLineString, Geometry splitter)
    {
        var parts = new List<Geometry>();
        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            if (multiLineString.GetGeometryN(i) is LineString line)
            {
                parts.AddRange(SplitLineString(line, splitter));
            }
        }

        return parts.Count > 0 ? parts : [multiLineString];
    }

    private static List<Coordinate> CollectSplitCoordinates(Geometry geometry)
    {
        var coordinates = new List<Coordinate>();
        CollectSplitCoordinates(geometry, coordinates);
        return coordinates;
    }

    private static void CollectSplitCoordinates(Geometry geometry, List<Coordinate> coordinates)
    {
        if (geometry.IsEmpty)
        {
            return;
        }

        switch (geometry)
        {
            case Point point:
                coordinates.Add(point.Coordinate);
                break;
            case MultiPoint multiPoint:
                for (var i = 0; i < multiPoint.NumGeometries; i++)
                {
                    if (multiPoint.GetGeometryN(i) is Point nestedPoint)
                    {
                        coordinates.Add(nestedPoint.Coordinate);
                    }
                }

                break;
            case GeometryCollection collection:
                for (var i = 0; i < collection.NumGeometries; i++)
                {
                    CollectSplitCoordinates(collection.GetGeometryN(i), coordinates);
                }

                break;
        }
    }
}
