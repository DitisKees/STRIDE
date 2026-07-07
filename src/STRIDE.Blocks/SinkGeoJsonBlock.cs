using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Text.Json;

namespace STRIDE.Blocks;

[StrideBlock("SinkGeoJson")]
public sealed class SinkGeoJsonBlock(
    string path,
    bool includeProperties = true,
    bool includeNullAndEmptyProperties = false) : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WritePropertyName("features");
        writer.WriteStartArray();

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (batch.Schema.GeometryFieldIndex < 0)
                {
                    throw new InvalidOperationException("SinkGeoJson requires an input geometry column.");
                }

                var geometryOrdinal = batch.Schema.GeometryFieldIndex;
                var geometryValues = batch.GeometryColumn(geometryOrdinal).Values;

                for (var row = 0; row < batch.RowCount; row++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "Feature");

                    writer.WritePropertyName("geometry");
                    if (geometryValues[row] is Geometry geometry)
                    {
                        WriteGeometry(writer, geometry);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }

                    writer.WritePropertyName("properties");
                    writer.WriteStartObject();
                    if (includeProperties)
                    {
                        for (var c = 0; c < batch.Schema.Fields.Length; c++)
                        {
                            if (c == geometryOrdinal)
                            {
                                continue;
                            }

                            var field = batch.Schema.Fields[c];
                            WriteProperty(
                                writer,
                                batch,
                                field.Name,
                                field.Type,
                                c,
                                row,
                                includeNullAndEmptyProperties);
                        }
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteGeometry(Utf8JsonWriter writer, Geometry geometry)
    {
        writer.WriteStartObject();

        switch (geometry)
        {
            case Point point:
                writer.WriteString("type", "Point");
                writer.WritePropertyName("coordinates");
                WriteCoordinate(writer, point.Coordinate);
                break;

            case LineString lineString:
                writer.WriteString("type", "LineString");
                writer.WritePropertyName("coordinates");
                WriteLineStringCoordinates(writer, lineString);
                break;

            case Polygon polygon:
                writer.WriteString("type", "Polygon");
                writer.WritePropertyName("coordinates");
                WritePolygonCoordinates(writer, polygon);
                break;

            case MultiPoint multiPoint:
                writer.WriteString("type", "MultiPoint");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var i = 0; i < multiPoint.NumGeometries; i++)
                {
                    var pointGeometry = (Point)multiPoint.GetGeometryN(i);
                    WriteCoordinate(writer, pointGeometry.Coordinate);
                }

                writer.WriteEndArray();
                break;

            case MultiLineString multiLineString:
                writer.WriteString("type", "MultiLineString");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var i = 0; i < multiLineString.NumGeometries; i++)
                {
                    WriteLineStringCoordinates(writer, (LineString)multiLineString.GetGeometryN(i));
                }

                writer.WriteEndArray();
                break;

            case MultiPolygon multiPolygon:
                writer.WriteString("type", "MultiPolygon");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var i = 0; i < multiPolygon.NumGeometries; i++)
                {
                    WritePolygonCoordinates(writer, (Polygon)multiPolygon.GetGeometryN(i));
                }

                writer.WriteEndArray();
                break;

            case GeometryCollection geometryCollection:
                writer.WriteString("type", "GeometryCollection");
                writer.WritePropertyName("geometries");
                writer.WriteStartArray();
                for (var i = 0; i < geometryCollection.NumGeometries; i++)
                {
                    WriteGeometry(writer, geometryCollection.GetGeometryN(i));
                }

                writer.WriteEndArray();
                break;

            default:
                throw new NotSupportedException($"Unsupported geometry type '{geometry.GeometryType}'.");
        }

        writer.WriteEndObject();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, Coordinate coordinate)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(coordinate.X);
        writer.WriteNumberValue(coordinate.Y);
        if (!double.IsNaN(coordinate.Z))
        {
            writer.WriteNumberValue(coordinate.Z);
        }

        writer.WriteEndArray();
    }

    private static void WriteLineStringCoordinates(Utf8JsonWriter writer, LineString lineString)
    {
        writer.WriteStartArray();
        foreach (var coordinate in lineString.Coordinates)
        {
            WriteCoordinate(writer, coordinate);
        }

        writer.WriteEndArray();
    }

    private static void WritePolygonCoordinates(Utf8JsonWriter writer, Polygon polygon)
    {
        writer.WriteStartArray();
        WriteLineStringCoordinates(writer, polygon.ExteriorRing);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            WriteLineStringCoordinates(writer, polygon.GetInteriorRingN(i));
        }

        writer.WriteEndArray();
    }

    private static void WriteProperty(
        Utf8JsonWriter writer,
        IRecordBatch batch,
        string fieldName,
        FieldType type,
        int ordinal,
        int row,
        bool includeNullAndEmptyProperties)
    {
        switch (type)
        {
            case FieldType.Boolean:
                writer.WritePropertyName(fieldName);
                writer.WriteBooleanValue(batch.Column<bool>(ordinal)[row]);
                break;
            case FieldType.Int32:
                writer.WritePropertyName(fieldName);
                writer.WriteNumberValue(batch.Column<int>(ordinal)[row]);
                break;
            case FieldType.Int64:
                writer.WritePropertyName(fieldName);
                writer.WriteNumberValue(batch.Column<long>(ordinal)[row]);
                break;
            case FieldType.Float64:
                writer.WritePropertyName(fieldName);
                writer.WriteNumberValue(batch.Column<double>(ordinal)[row]);
                break;
            case FieldType.Null:
                if (!includeNullAndEmptyProperties)
                {
                    return;
                }

                writer.WritePropertyName(fieldName);
                writer.WriteNullValue();
                break;
            default:
                var value = batch.GetValueAsString(ordinal, row);
                if (!includeNullAndEmptyProperties && string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                writer.WritePropertyName(fieldName);
                if (string.IsNullOrWhiteSpace(value))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStringValue(value);
                }
                break;
        }
    }
}
