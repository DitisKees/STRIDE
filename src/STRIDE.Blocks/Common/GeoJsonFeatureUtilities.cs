using NetTopologySuite.Geometries;
using System.Globalization;
using System.Text.Json;

namespace STRIDE.Blocks;

internal static class GeoJsonFeatureUtilities
{
    public static IReadOnlyList<CatalogFeatureRecord> ParseFeatureCollection(JsonElement root, string geometryColumn)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("GeoJSON payload must be a FeatureCollection with a features array.");
        }

        var rows = new List<CatalogFeatureRecord>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    attributes[property.Name] = ConvertJsonScalar(property.Value);
                }
            }

            Geometry? geometry = null;
            if (feature.TryGetProperty("geometry", out var geometryElement)
                && geometryElement.ValueKind == JsonValueKind.Object)
            {
                geometry = ParseGeometry(geometryElement);
            }

            rows.Add(new CatalogFeatureRecord(attributes, geometry));
        }

        return rows;
    }

    public static Geometry ParseGeometry(JsonElement geometryElement)
    {
        var type = geometryElement.GetProperty("type").GetString() ?? string.Empty;
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

        return type switch
        {
            "Point" => ParsePoint(geometryElement.GetProperty("coordinates"), factory),
            "LineString" => ParseLineString(geometryElement.GetProperty("coordinates"), factory),
            "Polygon" => ParsePolygon(geometryElement.GetProperty("coordinates"), factory),
            "MultiPoint" => ParseMultiPoint(geometryElement.GetProperty("coordinates"), factory),
            "MultiLineString" => ParseMultiLineString(geometryElement.GetProperty("coordinates"), factory),
            "MultiPolygon" => ParseMultiPolygon(geometryElement.GetProperty("coordinates"), factory),
            _ => throw new InvalidOperationException($"Unsupported GeoJSON geometry type '{type}'."),
        };
    }

    private static Point ParsePoint(JsonElement coordinates, GeometryFactory factory)
    {
        var x = coordinates[0].GetDouble();
        var y = coordinates[1].GetDouble();
        return factory.CreatePoint(new Coordinate(x, y));
    }

    private static LineString ParseLineString(JsonElement coordinates, GeometryFactory factory)
    {
        var points = coordinates
            .EnumerateArray()
            .Select(static point => new Coordinate(point[0].GetDouble(), point[1].GetDouble()))
            .ToArray();

        return factory.CreateLineString(points);
    }

    private static Polygon ParsePolygon(JsonElement coordinates, GeometryFactory factory)
    {
        var rings = coordinates
            .EnumerateArray()
            .Select(static ring => ring
                .EnumerateArray()
                .Select(static point => new Coordinate(point[0].GetDouble(), point[1].GetDouble()))
                .ToArray())
            .ToArray();

        if (rings.Length == 0)
        {
            return factory.CreatePolygon();
        }

        var shell = factory.CreateLinearRing(rings[0]);
        var holes = rings.Skip(1).Select(factory.CreateLinearRing).ToArray();
        return factory.CreatePolygon(shell, holes);
    }

    private static MultiPoint ParseMultiPoint(JsonElement coordinates, GeometryFactory factory)
    {
        var points = coordinates
            .EnumerateArray()
            .Select(point => ParsePoint(point, factory))
            .ToArray();

        return factory.CreateMultiPoint(points);
    }

    private static MultiLineString ParseMultiLineString(JsonElement coordinates, GeometryFactory factory)
    {
        var lines = coordinates
            .EnumerateArray()
            .Select(line => ParseLineString(line, factory))
            .ToArray();

        return factory.CreateMultiLineString(lines);
    }

    private static MultiPolygon ParseMultiPolygon(JsonElement coordinates, GeometryFactory factory)
    {
        var polygons = coordinates
            .EnumerateArray()
            .Select(polygon => ParsePolygon(polygon, factory))
            .ToArray();

        return factory.CreateMultiPolygon(polygons);
    }

    private static object? ConvertJsonScalar(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => CatalogRecordUtilities.ParseScalarString(value.GetString()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var int32Value) => int32Value,
            JsonValueKind.Number when value.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.GetRawText(),
        };
}
