using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Data.SQLite;

namespace STRIDE.Blocks;

[StrideBlock("SourceGeoPackage")]
public sealed class SourceGeoPackageBlock(
    string path,
    string? table = null,
    int batchSize = 1000,
    string geometryColumn = "geom") : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private Schema? _cachedSchema;
    private string? _resolvedTable;
    private string? _resolvedGeometryColumn;

    public Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        using var connection = OpenConnection();
        connection.Open();

        var tableName = ResolveTableName(connection);
        var gpkgGeometryColumn = ResolveGeoPackageGeometryColumn(connection, tableName);
        var fields = new List<FieldDef>();

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var columnName = reader.GetString(reader.GetOrdinal("name"));
            var declaredType = reader.IsDBNull(reader.GetOrdinal("type"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("type"));

            var type = string.Equals(columnName, gpkgGeometryColumn, StringComparison.OrdinalIgnoreCase)
                ? FieldType.Geometry
                : MapSqliteType(declaredType);

            fields.Add(new FieldDef(columnName, type, true));
        }

        if (!fields.Any(static field => field.Type == FieldType.Geometry))
        {
            fields.Add(new FieldDef(geometryColumn, FieldType.Geometry, true));
        }

        _resolvedTable = tableName;
        _resolvedGeometryColumn = gpkgGeometryColumn;
        _cachedSchema = new Schema(fields.ToImmutableArray());
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();
        var tableName = _resolvedTable ?? ResolveTableName(null);
        var geometryFieldName = schema.GeometryFieldIndex >= 0
            ? schema.Fields[schema.GeometryFieldIndex].Name
            : geometryColumn;

        using var connection = OpenConnection();
        connection.Open();

        var selectedColumns = schema.Fields
            .Where(field => field.Name != geometryFieldName || _resolvedGeometryColumn is not null)
            .Select(field => QuoteIdentifier(field.Name))
            .ToArray();

        var sql = selectedColumns.Length == 0
            ? $"SELECT * FROM {QuoteIdentifier(tableName)}"
            : $"SELECT {string.Join(", ", selectedColumns)} FROM {QuoteIdentifier(tableName)}";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        var buffer = new List<CatalogFeatureRecord>(_batchSize);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            Geometry? geometry = null;

            for (var col = 0; col < reader.FieldCount; col++)
            {
                var name = reader.GetName(col);
                if (reader.IsDBNull(col))
                {
                    attributes[name] = null;
                    continue;
                }

                if (_resolvedGeometryColumn is not null
                    && string.Equals(name, _resolvedGeometryColumn, StringComparison.OrdinalIgnoreCase))
                {
                    geometry = ReadGeoPackageGeometry(reader.GetFieldValue<byte[]>(col));
                    continue;
                }

                attributes[name] = ConvertSqliteScalar(reader.GetValue(col));
            }

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

    private SQLiteConnection OpenConnection()
        => new($"Data Source={path};Read Only=True");

    private string ResolveTableName(SQLiteConnection? connection)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedTable))
        {
            return _resolvedTable;
        }

        if (!string.IsNullOrWhiteSpace(table))
        {
            _resolvedTable = table;
            return _resolvedTable;
        }

        var ownsConnection = connection is null;
        connection ??= OpenConnection();
        if (ownsConnection)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM gpkg_contents WHERE data_type = 'features' ORDER BY table_name LIMIT 1;";
            var value = command.ExecuteScalar();
            if (value is string tableName && !string.IsNullOrWhiteSpace(tableName))
            {
                _resolvedTable = tableName;
                return tableName;
            }

            throw new InvalidOperationException("No feature table found in GeoPackage. Provide the 'table' parameter explicitly.");
        }
        finally
        {
            if (ownsConnection)
            {
                connection.Dispose();
            }
        }
    }

    private string? ResolveGeoPackageGeometryColumn(SQLiteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name FROM gpkg_geometry_columns WHERE table_name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", tableName);

        var value = command.ExecuteScalar();
        if (value is string columnName && !string.IsNullOrWhiteSpace(columnName))
        {
            return columnName;
        }

        return null;
    }

    private static Geometry? ReadGeoPackageGeometry(byte[] payload)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        if (payload[0] != 0x47 || payload[1] != 0x50)
        {
            return new WKBReader().Read(payload);
        }

        var flags = payload[3];
        var envelopeType = (flags >> 1) & 0b111;
        var envelopeBytes = envelopeType switch
        {
            0 => 0,
            1 => 32,
            2 => 48,
            3 => 48,
            4 => 64,
            _ => throw new InvalidOperationException($"Unsupported GeoPackage envelope indicator '{envelopeType}'."),
        };

        var headerSize = 8 + envelopeBytes;
        if (payload.Length <= headerSize)
        {
            return null;
        }

        var wkb = payload.AsSpan(headerSize).ToArray();
        var geometry = new WKBReader().Read(wkb);

        var littleEndian = (flags & 0b1) == 1;
        var srid = littleEndian
            ? BitConverter.ToInt32(payload, 4)
            : ReadInt32BigEndian(payload, 4);

        geometry.SRID = srid;
        return geometry;
    }

    private static int ReadInt32BigEndian(byte[] payload, int offset)
        => (payload[offset] << 24)
           | (payload[offset + 1] << 16)
           | (payload[offset + 2] << 8)
           | payload[offset + 3];

    private static object? ConvertSqliteScalar(object value)
        => value switch
        {
            DBNull => null,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            long longValue => longValue,
            double doubleValue => doubleValue,
            float floatValue => (double)floatValue,
            decimal decimalValue => (double)decimalValue,
            bool boolValue => boolValue,
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private static FieldType MapSqliteType(string declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType))
        {
            return FieldType.Utf8String;
        }

        var normalized = declaredType.Trim().ToUpperInvariant();

        if (normalized.Contains("BOOL"))
        {
            return FieldType.Boolean;
        }

        if (normalized.Contains("INT"))
        {
            return FieldType.Int64;
        }

        if (normalized.Contains("REAL")
            || normalized.Contains("FLOA")
            || normalized.Contains("DOUB")
            || normalized.Contains("DEC"))
        {
            return FieldType.Float64;
        }

        return FieldType.Utf8String;
    }

    private static string QuoteIdentifier(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

}
