using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Polygonize;
using System.Globalization;
using System.Xml.Linq;

namespace STRIDE.Blocks;

internal static class GmlGeometryUtilities
{
    public static CatalogFeatureRecord ParseFeatureElement(XElement featureElement, string geometryColumn)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Geometry? geometry = null;

        foreach (var child in featureElement.Elements())
        {
            var geometryElement = FindGeometryElement(child);
            if (geometryElement is not null)
            {
                geometry ??= ParseGeometry(geometryElement);
                continue;
            }

            var key = child.Name.LocalName;
            attributes[key] = CatalogRecordUtilities.ParseScalarString(child.Value);
        }

        return new CatalogFeatureRecord(attributes, geometry);
    }

    public static Geometry? ParseGeometry(XElement geometryElement)
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: ParseSrid(geometryElement));
        var geometry = geometryElement.Name.LocalName switch
        {
            "Point" => ParsePoint(geometryElement, geometryFactory),
            "LineString" => ParseLineString(geometryElement, geometryFactory),
            "Polygon" => ParsePolygon(geometryElement, geometryFactory),
            "MultiPoint" => ParseMultiPoint(geometryElement, geometryFactory),
            "MultiLineString" => ParseMultiLineString(geometryElement, geometryFactory),
            "MultiPolygon" => ParseMultiPolygon(geometryElement, geometryFactory),
            _ => ParseFromWktFallback(geometryElement, geometryFactory),
        };

        if (geometry is not null)
        {
            geometry.SRID = geometryFactory.SRID;
        }

        return geometry;
    }

    private static XElement? FindGeometryElement(XElement element)
    {
        if (IsGeometryElement(element.Name.LocalName))
        {
            return element;
        }

        return element
            .Descendants()
            .FirstOrDefault(descendant => IsGeometryElement(descendant.Name.LocalName));
    }

    private static bool IsGeometryElement(string localName)
        => localName is "Point"
            or "LineString"
            or "Polygon"
            or "MultiPoint"
            or "MultiLineString"
            or "MultiPolygon";

    private static Point ParsePoint(XElement element, GeometryFactory factory)
    {
        var coordinate = ParseFirstCoordinate(element);
        return factory.CreatePoint(coordinate);
    }

    private static LineString ParseLineString(XElement element, GeometryFactory factory)
    {
        var coordinates = ParseCoordinateSequence(element);
        return factory.CreateLineString(coordinates);
    }

    private static Polygon ParsePolygon(XElement element, GeometryFactory factory)
    {
        var shellRing = element
            .Descendants()
            .FirstOrDefault(static descendant => descendant.Name.LocalName == "exterior")?
            .Descendants()
            .FirstOrDefault(static descendant => descendant.Name.LocalName == "LinearRing");

        var holeRings = element
            .Descendants()
            .Where(static descendant => descendant.Name.LocalName == "interior")
            .Select(static interior => interior
                .Descendants()
                .FirstOrDefault(static descendant => descendant.Name.LocalName == "LinearRing"))
            .Where(static ring => ring is not null)
            .Cast<XElement>()
            .ToArray();

        if (shellRing is null)
        {
            var direct = ParseCoordinateSequence(element);
            return factory.CreatePolygon(factory.CreateLinearRing(direct));
        }

        var shell = factory.CreateLinearRing(ParseCoordinateSequence(shellRing));
        var holes = holeRings
            .Select(ring => factory.CreateLinearRing(ParseCoordinateSequence(ring)))
            .ToArray();

        return factory.CreatePolygon(shell, holes);
    }

    private static MultiPoint ParseMultiPoint(XElement element, GeometryFactory factory)
    {
        var points = element
            .Descendants()
            .Where(static descendant => descendant.Name.LocalName == "Point")
            .Select(pointElement => ParsePoint(pointElement, factory))
            .ToArray();

        return factory.CreateMultiPoint(points);
    }

    private static Geometry ParseMultiLineString(XElement element, GeometryFactory factory)
    {
        var lines = element
            .Descendants()
            .Where(static descendant => descendant.Name.LocalName == "LineString")
            .Select(lineElement => ParseLineString(lineElement, factory))
            .ToArray();

        return lines.Length == 1
            ? lines[0]
            : factory.CreateMultiLineString(lines);
    }

    private static Geometry ParseMultiPolygon(XElement element, GeometryFactory factory)
    {
        var polygons = element
            .Descendants()
            .Where(static descendant => descendant.Name.LocalName == "Polygon")
            .Select(polygonElement => ParsePolygon(polygonElement, factory))
            .ToArray();

        if (polygons.Length == 0)
        {
            return factory.CreateMultiPolygon();
        }

        if (polygons.Length == 1)
        {
            return polygons[0];
        }

        var polygonizer = new Polygonizer();
        foreach (var polygon in polygons)
        {
            polygonizer.Add(polygon.Boundary);
        }

        var polygonized = polygonizer.GetPolygons().OfType<Polygon>().ToArray();
        return polygonized.Length > 0
            ? factory.CreateMultiPolygon(polygonized)
            : factory.CreateMultiPolygon(polygons);
    }

    private static Geometry? ParseFromWktFallback(XElement element, GeometryFactory factory)
    {
        var text = element.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var geometry = new WKTReader().Read(text);
            geometry.SRID = factory.SRID;
            return geometry;
        }
        catch
        {
            return null;
        }
    }

    private static Coordinate ParseFirstCoordinate(XElement element)
    {
        var sequence = ParseCoordinateSequence(element);
        return sequence.Length > 0 ? sequence[0] : new Coordinate(0, 0);
    }

    private static Coordinate[] ParseCoordinateSequence(XElement element)
    {
        var posList = element
            .Descendants()
            .FirstOrDefault(static descendant => descendant.Name.LocalName == "posList")?
            .Value;

        if (!string.IsNullOrWhiteSpace(posList))
        {
            return ParsePosList(posList);
        }

        var positions = element
            .Descendants()
            .Where(static descendant => descendant.Name.LocalName == "pos")
            .Select(descendant => ParsePosition(descendant.Value))
            .ToArray();

        if (positions.Length > 0)
        {
            return positions;
        }

        var coordinates = element
            .Descendants()
            .FirstOrDefault(static descendant => descendant.Name.LocalName == "coordinates")?
            .Value;

        return string.IsNullOrWhiteSpace(coordinates)
            ? Array.Empty<Coordinate>()
            : ParseCoordinateList(coordinates);
    }

    private static Coordinate ParsePosition(string text)
    {
        var parts = text
            .Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return new Coordinate(0, 0);
        }

        var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return new Coordinate(x, y);
    }

    private static Coordinate[] ParsePosList(string text)
    {
        var parts = text
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var coordinates = new List<Coordinate>(parts.Length / 2);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var x = double.Parse(parts[i], CultureInfo.InvariantCulture);
            var y = double.Parse(parts[i + 1], CultureInfo.InvariantCulture);
            coordinates.Add(new Coordinate(x, y));
        }

        return coordinates.ToArray();
    }

    private static Coordinate[] ParseCoordinateList(string text)
    {
        var pairs = text
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var coordinates = new List<Coordinate>(pairs.Length);
        foreach (var pair in pairs)
        {
            var parts = pair.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
            coordinates.Add(new Coordinate(x, y));
        }

        return coordinates.ToArray();
    }

    private static int ParseSrid(XElement element)
    {
        var srsName = element.Attribute("srsName")?.Value
            ?? element
                .AncestorsAndSelf()
                .Select(ancestor => ancestor.Attribute("srsName")?.Value)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(srsName))
        {
            return 4326;
        }

        if (int.TryParse(srsName, out var srid))
        {
            return srid;
        }

        var epsgPart = srsName
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return int.TryParse(epsgPart, out srid) ? srid : 4326;
    }
}
