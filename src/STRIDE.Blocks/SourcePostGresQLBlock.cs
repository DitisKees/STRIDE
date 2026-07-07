using NetTopologySuite.Geometries;
using Npgsql;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("SourcePostGresQL")]
public class SourcePostGresQLBlock(string connectionString, string query, int batchSize = 1000) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private Schema? _cachedSchema;

    public Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        using var dataSource = CreateDataSource();
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(query, connection);
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SchemaOnly);

        var fields = reader.GetColumnSchema()
            .Select(static column => new FieldDef(
                column.ColumnName ?? "column",
                MapFieldType(column),
                true))
            .ToImmutableArray();

        _cachedSchema = new Schema(fields);
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();

        await using var dataSource = CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var buffer = CreateBuffer(schema.Fields.Length);
        var rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AppendRow(buffer, reader, schema);
            rowCount++;

            if (rowCount >= _batchSize)
            {
                yield return BuildBatch(schema, buffer, rowCount);
                buffer = CreateBuffer(schema.Fields.Length);
                rowCount = 0;
            }
        }

        if (rowCount > 0)
        {
            yield return BuildBatch(schema, buffer, rowCount);
        }
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        return builder.Build();
    }

    private NpgsqlDataSource CreateDataSource()
        => CreateDataSource(connectionString);

    private static FieldType MapFieldType(DbColumn column)
    {
        var dataType = column.DataType;
        if (dataType == typeof(bool))
        {
            return FieldType.Boolean;
        }

        if (dataType == typeof(short) || dataType == typeof(int))
        {
            return FieldType.Int32;
        }

        if (dataType == typeof(long))
        {
            return FieldType.Int64;
        }

        if (dataType == typeof(float) || dataType == typeof(double) || dataType == typeof(decimal))
        {
            return FieldType.Float64;
        }

        if (dataType == typeof(DateTime) || dataType == typeof(DateTimeOffset))
        {
            return FieldType.DateTimeUtc;
        }

        if (typeof(Geometry).IsAssignableFrom(dataType) || string.Equals(column.DataTypeName, "geometry", StringComparison.OrdinalIgnoreCase))
        {
            return FieldType.Geometry;
        }

        return FieldType.Utf8String;
    }

    private static object[] CreateBuffer(int columnCount)
    {
        var buffer = new object[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            buffer[i] = new List<object?>();
        }

        return buffer;
    }

    private static void AppendRow(object[] buffer, NpgsqlDataReader reader, Schema schema)
    {
        for (var c = 0; c < schema.Fields.Length; c++)
        {
            var list = (List<object?>)buffer[c];
            if (reader.IsDBNull(c))
            {
                list.Add(null);
                continue;
            }

            list.Add(reader.GetValue(c));
        }
    }

    private static RecordBatch BuildBatch(Schema schema, object[] buffer, int rowCount)
    {
        var columns = new object?[schema.Fields.Length];
        for (var c = 0; c < schema.Fields.Length; c++)
        {
            var values = (List<object?>)buffer[c];
            columns[c] = schema.Fields[c].Type switch
            {
                FieldType.Boolean => values.Select(static v => v is bool b && b).ToArray(),
                FieldType.Int32 => values.Select(static v => Convert.ToInt32(v ?? 0, CultureInfo.InvariantCulture)).ToArray(),
                FieldType.Int64 => values.Select(static v => Convert.ToInt64(v ?? 0, CultureInfo.InvariantCulture)).ToArray(),
                FieldType.Float64 => values.Select(static v => Convert.ToDouble(v ?? 0d, CultureInfo.InvariantCulture)).ToArray(),
                FieldType.Geometry => new GeometryColumn(values.Select(static v => v as Geometry).ToArray()),
                _ => RecordBatch.CreateUtf8Column(values.Select(static v => Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray()),
            };
        }

        return new RecordBatch(schema, rowCount, columns);
    }
}

[StrideBlock("SourcePostGis")]
public sealed class SourcePostGisBlock : SourcePostGresQLBlock
{
    public SourcePostGisBlock(string connectionString, string query, int batchSize = 1000)
        : base(connectionString, query, batchSize)
    {
    }
}
