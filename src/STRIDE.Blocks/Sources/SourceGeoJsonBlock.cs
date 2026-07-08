using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace STRIDE.Blocks;

[StrideBlock("SourceGeoJson")]
public sealed class SourceGeoJsonBlock(string path, string geometryColumn = "geom", int batchSize = 1000) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private IReadOnlyList<FeatureRecord>? _cachedRecords;
    private Schema? _cachedSchema;

    public Schema DeriveOutputSchema()
    {
        EnsureLoaded();
        return _cachedSchema!;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureLoaded();

        var records = _cachedRecords!;
        var schema = _cachedSchema!;
        for (var i = 0; i < records.Count; i += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_batchSize, records.Count - i);
            var slice = new FeatureRecord[count];
            for (var row = 0; row < count; row++)
            {
                slice[row] = records[i + row];
            }

            yield return BuildBatch(schema, slice);
            await Task.Yield();
        }
    }

    private void EnsureLoaded()
    {
        if (_cachedRecords is not null && _cachedSchema is not null)
        {
            return;
        }

        var records = LoadRecords(path);
        var propertyTypes = InferPropertyTypes(records);

        var fields = new List<FieldDef>(propertyTypes.Count + 1);
        foreach (var (name, type) in propertyTypes)
        {
            fields.Add(new FieldDef(name, type, true));
        }

        fields.Add(new FieldDef(geometryColumn, FieldType.Geometry, true));

        _cachedRecords = records;
        _cachedSchema = new Schema(fields.ToImmutableArray());
    }

    private static IReadOnlyList<FeatureRecord> LoadRecords(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("SourceGeoJson expects a GeoJSON FeatureCollection with a 'features' array.");
        }

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var rows = new List<FeatureRecord>();

        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("properties", out var propertyObject) && propertyObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in propertyObject.EnumerateObject())
                {
                    properties[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => property.Value.GetRawText(),
                    };
                }
            }

            Geometry? geometry = null;
            if (feature.TryGetProperty("geometry", out var geometryElement) && geometryElement.ValueKind is JsonValueKind.Object)
            {
                geometry = ParseGeometry(geometryElement, geometryFactory);
            }

            rows.Add(new FeatureRecord(properties, geometry));
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, FieldType> InferPropertyTypes(IEnumerable<FeatureRecord> records)
    {
        var valuesByField = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var (name, value) in record.Properties)
            {
                if (!valuesByField.TryGetValue(name, out var values))
                {
                    values = [];
                    valuesByField[name] = values;
                }

                values.Add(value);
            }
        }

        var result = new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in valuesByField)
        {
            var hasValue = false;
            var allInt = true;
            var allDouble = true;
            var allBool = true;
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                hasValue = true;
                if (allInt && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    allInt = false;
                }

                if (allDouble && !double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
                {
                    allDouble = false;
                }

                if (allBool && !bool.TryParse(value, out _))
                {
                    allBool = false;
                }
            }

            result[name] = !hasValue
                ? FieldType.Utf8String
                : allInt
                    ? FieldType.Int64
                    : allDouble
                        ? FieldType.Float64
                        : allBool
                            ? FieldType.Boolean
                            : FieldType.Utf8String;
        }

        return result;
    }

    private static RecordBatch BuildBatch(Schema schema, IReadOnlyList<FeatureRecord> records)
    {
        var columns = new object?[schema.Fields.Length];
        var geometryOrdinal = schema.GeometryFieldIndex;

        for (var c = 0; c < schema.Fields.Length; c++)
        {
            if (c == geometryOrdinal)
            {
                var geometryValues = new Geometry?[records.Count];
                for (var r = 0; r < records.Count; r++)
                {
                    geometryValues[r] = records[r].Geometry;
                }

                columns[c] = new GeometryColumn(geometryValues);
                continue;
            }

            var field = schema.Fields[c];
            var fieldName = field.Name;

            switch (field.Type)
            {
                case FieldType.Boolean:
                    {
                        var values = new bool[records.Count];
                        for (var r = 0; r < records.Count; r++)
                        {
                            if (records[r].Properties.TryGetValue(fieldName, out var value)
                                && !string.IsNullOrWhiteSpace(value)
                                && bool.TryParse(value, out var parsed))
                            {
                                values[r] = parsed;
                            }
                        }

                        columns[c] = values;
                        break;
                    }

                case FieldType.Int32:
                    {
                        var values = new int[records.Count];
                        for (var r = 0; r < records.Count; r++)
                        {
                            if (records[r].Properties.TryGetValue(fieldName, out var value)
                                && !string.IsNullOrWhiteSpace(value)
                                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            {
                                values[r] = parsed;
                            }
                        }

                        columns[c] = values;
                        break;
                    }

                case FieldType.Int64:
                    {
                        var values = new long[records.Count];
                        for (var r = 0; r < records.Count; r++)
                        {
                            if (records[r].Properties.TryGetValue(fieldName, out var value)
                                && !string.IsNullOrWhiteSpace(value)
                                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                            {
                                values[r] = parsed;
                            }
                        }

                        columns[c] = values;
                        break;
                    }

                case FieldType.Float64:
                    {
                        var values = new double[records.Count];
                        for (var r = 0; r < records.Count; r++)
                        {
                            if (records[r].Properties.TryGetValue(fieldName, out var value)
                                && !string.IsNullOrWhiteSpace(value)
                                && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                            {
                                values[r] = parsed;
                            }
                        }

                        columns[c] = values;
                        break;
                    }

                default:
                    {
                        var values = new string?[records.Count];
                        for (var r = 0; r < records.Count; r++)
                        {
                            values[r] = records[r].Properties.TryGetValue(fieldName, out var value) ? value : null;
                        }

                        columns[c] = RecordBatch.CreateUtf8Column(values);
                        break;
                    }
            }
        }

        return new RecordBatch(schema, records.Count, columns);
    }

    private static Geometry ParseGeometry(JsonElement geometryElement, GeometryFactory geometryFactory)
    {
        var type = geometryElement.GetProperty("type").GetString() ?? string.Empty;
        return type switch
        {
            "Point" => ParsePoint(geometryElement.GetProperty("coordinates"), geometryFactory),
            "LineString" => ParseLineString(geometryElement.GetProperty("coordinates"), geometryFactory),
            "Polygon" => ParsePolygon(geometryElement.GetProperty("coordinates"), geometryFactory),
            _ => throw new InvalidOperationException($"SourceGeoJson currently supports Point/LineString/Polygon geometries, but got '{type}'."),
        };
    }

    private static Point ParsePoint(JsonElement coordinates, GeometryFactory geometryFactory)
    {
        var x = coordinates[0].GetDouble();
        var y = coordinates[1].GetDouble();
        return geometryFactory.CreatePoint(new Coordinate(x, y));
    }

    private static LineString ParseLineString(JsonElement coordinates, GeometryFactory geometryFactory)
    {
        var values = coordinates
            .EnumerateArray()
            .Select(static point => new Coordinate(point[0].GetDouble(), point[1].GetDouble()))
            .ToArray();
        return geometryFactory.CreateLineString(values);
    }

    private static Polygon ParsePolygon(JsonElement coordinates, GeometryFactory geometryFactory)
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
            return geometryFactory.CreatePolygon();
        }

        var shell = geometryFactory.CreateLinearRing(rings[0]);
        var holes = rings.Skip(1).Select(geometryFactory.CreateLinearRing).ToArray();
        return geometryFactory.CreatePolygon(shell, holes);
    }

    private sealed record FeatureRecord(IReadOnlyDictionary<string, string?> Properties, Geometry? Geometry);
}
