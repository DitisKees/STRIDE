using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace STRIDE.Blocks;

[StrideBlock("SourceShapefile")]
public sealed class SourceShapefileBlock(
    string path,
    int srid = 4326,
    string geometryColumn = "geom",
    int batchSize = 1000) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private Schema? _cachedSchema;
    private IReadOnlyList<DbfField>? _dbfFields;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>>? _dbfRows;

    public Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        EnsureLoadedDbf();

        var fields = _dbfFields!
            .Select(field => new FieldDef(field.Name, field.Type, true))
            .ToList();

        fields.Add(new FieldDef(geometryColumn, FieldType.Geometry, true));
        _cachedSchema = new Schema(fields.ToImmutableArray());
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();
        EnsureLoadedDbf();

        var shpPath = ResolveWithExtension(path, ".shp");
        var shxPath = ResolveWithExtension(path, ".shx");

        var shpBytes = MapReadAllBytes(shpPath);
        var offsets = ReadShxOffsets(MapReadAllBytes(shxPath));

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: srid);
        var count = Math.Min(offsets.Count, _dbfRows!.Count);
        var buffer = new List<CatalogFeatureRecord>(_batchSize);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = new Dictionary<string, object?>(_dbfRows[index], StringComparer.OrdinalIgnoreCase);
            var geometry = ParseShapeGeometry(shpBytes, offsets[index], geometryFactory);
            buffer.Add(new CatalogFeatureRecord(attributes, geometry));

            if (buffer.Count < _batchSize)
            {
                continue;
            }

            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
            buffer.Clear();
            await Task.Yield();
        }

        if (buffer.Count > 0)
        {
            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
        }
    }

    private void EnsureLoadedDbf()
    {
        if (_dbfFields is not null && _dbfRows is not null)
        {
            return;
        }

        var dbfPath = ResolveWithExtension(path, ".dbf");
        (_dbfFields, _dbfRows) = ReadDbf(dbfPath);
    }

    private static (IReadOnlyList<DbfField> Fields, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) ReadDbf(string dbfPath)
    {
        using var stream = File.OpenRead(dbfPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        _ = reader.ReadByte();
        _ = reader.ReadBytes(3);
        var recordCount = reader.ReadInt32();
        var headerLength = reader.ReadUInt16();
        var recordLength = reader.ReadUInt16();
        _ = reader.ReadBytes(20);

        var fields = new List<DbfField>();
        while (stream.Position < headerLength)
        {
            var marker = reader.ReadByte();
            if (marker == 0x0D)
            {
                break;
            }

            stream.Position--;
            var descriptor = reader.ReadBytes(32);

            var nameBytes = descriptor.AsSpan(0, 11);
            var nullTerminator = nameBytes.IndexOf((byte)0);
            if (nullTerminator >= 0)
            {
                nameBytes = nameBytes[..nullTerminator];
            }

            var fieldName = Encoding.ASCII.GetString(nameBytes).Trim();
            var fieldType = (char)descriptor[11];
            var fieldLength = descriptor[16];
            var decimalCount = descriptor[17];

            fields.Add(new DbfField(fieldName, MapDbfFieldType(fieldType, decimalCount), fieldLength, fieldType, decimalCount));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(recordCount);
        for (var rowIndex = 0; rowIndex < recordCount; rowIndex++)
        {
            var record = reader.ReadBytes(recordLength);
            if (record.Length < recordLength)
            {
                break;
            }

            if (record[0] == (byte)'*')
            {
                continue;
            }

            var values = new Dictionary<string, object?>(fields.Count, StringComparer.OrdinalIgnoreCase);
            var offset = 1;

            foreach (var field in fields)
            {
                var raw = Encoding.GetEncoding(1252)
                    .GetString(record, offset, field.Length)
                    .Trim();

                values[field.Name] = ConvertDbfValue(field, raw);
                offset += field.Length;
            }

            rows.Add(values);
        }

        return (fields, rows);
    }

    private static object? ConvertDbfValue(DbfField field, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        switch (field.DbfType)
        {
            case 'L':
                return raw.Equals("Y", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("T", StringComparison.OrdinalIgnoreCase);

            case 'N':
            case 'F':
            case 'B':
            case 'I':
                if (field.DecimalCount == 0 && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    if (longValue is >= int.MinValue and <= int.MaxValue)
                    {
                        return (int)longValue;
                    }

                    return longValue;
                }

                if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    return doubleValue;
                }

                return raw;

            default:
                return raw;
        }
    }

    private static IReadOnlyList<int> ReadShxOffsets(byte[] shx)
    {
        if (shx.Length < 100)
        {
            return Array.Empty<int>();
        }

        var count = (shx.Length - 100) / 8;
        var offsets = new int[count];

        for (var i = 0; i < count; i++)
        {
            var offset = 100 + (i * 8);
            var words = BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(offset, 4));
            offsets[i] = words * 2;
        }

        return offsets;
    }

    private static Geometry? ParseShapeGeometry(byte[] shp, int recordOffset, GeometryFactory factory)
    {
        if (recordOffset + 12 > shp.Length)
        {
            return null;
        }

        var contentOffset = recordOffset + 8;
        var shapeType = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(contentOffset, 4));

        return shapeType switch
        {
            0 => null,
            1 or 11 or 21 => ParsePoint(shp, contentOffset + 4, factory),
            3 or 13 or 23 => ParsePolyline(shp, contentOffset + 4, factory),
            5 or 15 or 25 => ParsePolygon(shp, contentOffset + 4, factory),
            8 or 18 or 28 => ParseMultiPoint(shp, contentOffset + 4, factory),
            _ => null,
        };
    }

    private static Point ParsePoint(byte[] data, int offset, GeometryFactory factory)
    {
        var x = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset, 8));
        var y = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset + 8, 8));
        return factory.CreatePoint(new Coordinate(x, y));
    }

    private static Geometry ParsePolyline(byte[] data, int offset, GeometryFactory factory)
    {
        var numParts = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 32, 4));
        var numPoints = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 36, 4));

        var partsOffset = offset + 40;
        var pointsOffset = partsOffset + (numParts * 4);

        var partStarts = new int[numParts + 1];
        for (var i = 0; i < numParts; i++)
        {
            partStarts[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(partsOffset + (i * 4), 4));
        }

        partStarts[numParts] = numPoints;

        var lines = new List<LineString>(numParts);
        for (var partIndex = 0; partIndex < numParts; partIndex++)
        {
            var start = partStarts[partIndex];
            var end = partStarts[partIndex + 1];
            var coordinates = ReadPoints(data, pointsOffset, start, end);
            if (coordinates.Length > 1)
            {
                lines.Add(factory.CreateLineString(coordinates));
            }
        }

        return lines.Count == 1
            ? lines[0]
            : factory.CreateMultiLineString(lines.ToArray());
    }

    private static Geometry ParsePolygon(byte[] data, int offset, GeometryFactory factory)
    {
        var numParts = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 32, 4));
        var numPoints = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 36, 4));

        var partsOffset = offset + 40;
        var pointsOffset = partsOffset + (numParts * 4);

        var partStarts = new int[numParts + 1];
        for (var i = 0; i < numParts; i++)
        {
            partStarts[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(partsOffset + (i * 4), 4));
        }

        partStarts[numParts] = numPoints;

        var rings = new List<LinearRing>(numParts);
        for (var partIndex = 0; partIndex < numParts; partIndex++)
        {
            var start = partStarts[partIndex];
            var end = partStarts[partIndex + 1];
            var coordinates = ReadPoints(data, pointsOffset, start, end);
            if (coordinates.Length < 4)
            {
                continue;
            }

            if (!coordinates[0].Equals2D(coordinates[^1]))
            {
                Array.Resize(ref coordinates, coordinates.Length + 1);
                coordinates[^1] = coordinates[0].Copy();
            }

            rings.Add(factory.CreateLinearRing(coordinates));
        }

        if (rings.Count == 0)
        {
            return factory.CreatePolygon();
        }

        var shells = new List<LinearRing>();
        var holes = new List<LinearRing>();

        foreach (var ring in rings)
        {
            if (Orientation.IsCCW(ring.CoordinateSequence))
            {
                holes.Add(ring);
            }
            else
            {
                shells.Add(ring);
            }
        }

        if (shells.Count == 0)
        {
            shells.Add(rings[0]);
            holes = rings.Skip(1).ToList();
        }

        var polygons = new List<Polygon>(shells.Count);
        foreach (var shell in shells)
        {
            var shellPolygon = factory.CreatePolygon(shell);
            var shellHoles = holes
                .Where(hole => shellPolygon.Covers(factory.CreatePoint(hole.Coordinate)))
                .ToArray();

            polygons.Add(factory.CreatePolygon(shell, shellHoles));
        }

        return polygons.Count == 1
            ? polygons[0]
            : factory.CreateMultiPolygon(polygons.ToArray());
    }

    private static MultiPoint ParseMultiPoint(byte[] data, int offset, GeometryFactory factory)
    {
        var numPoints = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 32, 4));
        var pointsOffset = offset + 36;

        var points = new Point[numPoints];
        for (var i = 0; i < numPoints; i++)
        {
            var x = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(pointsOffset + (i * 16), 8));
            var y = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(pointsOffset + (i * 16) + 8, 8));
            points[i] = factory.CreatePoint(new Coordinate(x, y));
        }

        return factory.CreateMultiPoint(points);
    }

    private static Coordinate[] ReadPoints(byte[] data, int pointsOffset, int start, int end)
    {
        var length = Math.Max(0, end - start);
        var coordinates = new Coordinate[length];

        for (var i = 0; i < length; i++)
        {
            var pointOffset = pointsOffset + ((start + i) * 16);
            var x = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(pointOffset, 8));
            var y = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(pointOffset + 8, 8));
            coordinates[i] = new Coordinate(x, y);
        }

        return coordinates;
    }

    private static byte[] MapReadAllBytes(string filePath)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var stream = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        var bytes = new byte[stream.Length];

        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return bytes;
    }

    private static string ResolveWithExtension(string inputPath, string extension)
    {
        if (inputPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return inputPath;
        }

        return Path.ChangeExtension(inputPath, extension);
    }

    private static FieldType MapDbfFieldType(char type, byte decimalCount)
        => type switch
        {
            'L' => FieldType.Boolean,
            'N' or 'F' or 'B' or 'I' when decimalCount == 0 => FieldType.Int64,
            'N' or 'F' or 'B' or 'I' => FieldType.Float64,
            _ => FieldType.Utf8String,
        };

    private sealed record DbfField(string Name, FieldType Type, byte Length, char DbfType, byte DecimalCount);
}
