using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using STRIDE.Blocks.Models;

namespace STRIDE.Blocks;

[StrideBlock("PostGreSqlSource")]
public sealed class PostGreSqlSource : ISourceBlock
{
    // Definieer deze kleine struct boven of binnen je namespace voor binaire stringopslag tijdens de batch-opbouw
    public sealed class BinaryStringContainer
    {
        public int[] Offsets { get; set; } = null!;
        public byte[] Data { get; set; } = null!;
        public int CurrentOffset { get; set; }
    }
    private readonly string _connectionString;
    private readonly string _query;
    private readonly int _batchSize;
    private Schema? _derivedSchema;
    private string[]? _pgColumnTypes;

    public PostGreSqlSource(Dictionary<string, string> parameters)
    {
        _connectionString = parameters["connectionString"].Trim('"');
        _query = parameters["query"].Trim('"');
        _batchSize = parameters.TryGetValue("batchSize", out var b) && int.TryParse(b.Trim('"'), out var parsedB) ? parsedB : 4096;
        if (_batchSize <= 0) _batchSize = 4096;
    }

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        if (_derivedSchema != null) return _derivedSchema;
        return DiscoverSchemaAsync().GetAwaiter().GetResult();
    }

    private async Task<Schema> DiscoverSchemaAsync()
    {
        string schemaQuery = $"SELECT * FROM ({_query.TrimEnd(';')}) AS __stride_schema_subquery LIMIT 0";

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
        dataSourceBuilder.UseNetTopologySuite(); // Cruciaal voor geometrie-herkenning
        await using var dataSource = dataSourceBuilder.Build();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(schemaQuery, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var fields = ImmutableArray.CreateBuilder<FieldDef>();
        var columnSchema = await reader.GetColumnSchemaAsync();

        var pgTypes = new string[columnSchema.Count];
        for (int i = 0; i < columnSchema.Count; i++)
        {
            var col = columnSchema[i];
            string pgTypeName = col.DataTypeName ?? "text";
            int dotIndex = pgTypeName.LastIndexOf('.');
            if (dotIndex >= 0) pgTypeName = pgTypeName[(dotIndex + 1)..];
            pgTypeName = pgTypeName.ToLowerInvariant();
            pgTypes[i] = pgTypeName;

            var fieldType = MapPostgresTypeToStride(col.ColumnName, pgTypeName);
            fields.Add(new FieldDef(col.ColumnName, fieldType, Nullable: true));
        }

        _pgColumnTypes = pgTypes;
        _derivedSchema = new Schema(fields.ToImmutable());
        return _derivedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> StreamAsync(BlockContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        var schema = _derivedSchema ?? await DiscoverSchemaAsync();
        var pgTypes = _pgColumnTypes;
        if (pgTypes == null)
        {
            await DiscoverSchemaAsync();
            pgTypes = _pgColumnTypes!;
        }
        string copyQuery = $"COPY ({_query.TrimEnd(';')}) TO STDOUT (FORMAT BINARY)";

        var connStringBuilder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            CommandTimeout = 0
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStringBuilder.ConnectionString);
        dataSourceBuilder.UseNetTopologySuite();
        await using var dataSource = dataSourceBuilder.Build();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var reader = await connection.BeginBinaryExportAsync(copyQuery, ct);

        int colCount = schema.Fields.Length;
        int localRowCount = 0;
        int totalRowCount = 0;

        var columnBuffers = new Array[colCount];
        var nullBitmaps = new bool[colCount][]; // Bitmaps om NULL-waarden per kolom bij te houden

        for (int i = 0; i < colCount; i++)
        {
            columnBuffers[i] = RentArrayForType(schema.Fields[i].Type, _batchSize);
            nullBitmaps[i] = ArrayPool<bool>.Shared.Rent(_batchSize);
        }

        while (await reader.StartRowAsync(ct) != -1)
        {
            if (ct.IsCancellationRequested) yield break;

            for (int colIndex = 0; colIndex < colCount; colIndex++)
            {
                // Controleer of de huidige kolom NULL is via de ingebouwde Npgsql metadata-indicator
                if (reader.IsNull)
                {
                    nullBitmaps[colIndex][localRowCount] = true;
                    // Skip de kolom in de binaire stream zodat de interne pointer doorschuift
                    await reader.SkipAsync(ct);

                    if (schema.Fields[colIndex].Type == FieldType.Utf8String)
                    {
                        var container = ((BinaryStringContainer[])columnBuffers[colIndex])[0];
                        container.Offsets[localRowCount] = container.CurrentOffset;
                        container.Offsets[localRowCount + 1] = container.CurrentOffset;
                    }
                    continue;
                }

                nullBitmaps[colIndex][localRowCount] = false;
                var fieldType = schema.Fields[colIndex].Type;
                await ReadColumnValueAsync(reader, fieldType, pgTypes[colIndex], columnBuffers[colIndex], localRowCount, ct);
            }

            localRowCount++;
            totalRowCount++;
            if (totalRowCount % _batchSize == 0)
                Console.WriteLine($"[PostGreSqlSource] Processed {totalRowCount} rows.");

            if (localRowCount == _batchSize)
            {
                yield return BuildBatch(schema, columnBuffers, nullBitmaps, localRowCount);

                localRowCount = 0;
                columnBuffers = new Array[colCount];
                nullBitmaps = new bool[colCount][];
                for (int i = 0; i < colCount; i++)
                {
                    columnBuffers[i] = RentArrayForType(schema.Fields[i].Type, _batchSize);
                    nullBitmaps[i] = ArrayPool<bool>.Shared.Rent(_batchSize);
                }
            }
        }

        if (totalRowCount % _batchSize != 0)
            Console.WriteLine($"[PostGreSqlSource] Processed {totalRowCount} rows.");

        if (localRowCount > 0)
        {
            yield return BuildBatch(schema, columnBuffers, nullBitmaps, localRowCount);
        }
        else
        {
            for (int i = 0; i < colCount; i++)
            {
                ReturnArrayForType(schema.Fields[i].Type, columnBuffers[i]);
                ArrayPool<bool>.Shared.Return(nullBitmaps[i]);
            }
        }
    }

    private static FieldType MapPostgresTypeToStride(string colName, string pgTypeName)
    {
        int dotIndex = pgTypeName.LastIndexOf('.');
        if (dotIndex >= 0) pgTypeName = pgTypeName[(dotIndex + 1)..];

        return pgTypeName.ToLowerInvariant() switch
        {
            "bool" or "boolean" => FieldType.Boolean,
            "int8" or "bigint" => FieldType.Int64,
            "int4" or "integer" => FieldType.Int32,
            "float8" or "double precision" => FieldType.Float64,
            "numeric" or "decimal" => FieldType.Float64,
            "varchar" or "text" or "character varying" or "char" => FieldType.Utf8String,
            "date" or "timestamp" or "timestamptz" => FieldType.DateTimeUtc,
            "geometry" => FieldType.Geometry,
            _ => FieldType.Utf8String
        };
    }

    private static Array RentArrayForType(FieldType type, int size)
    {
        return type switch
        {
            FieldType.Boolean => ArrayPool<bool>.Shared.Rent(size),
            FieldType.Int64 => ArrayPool<long>.Shared.Rent(size),
            FieldType.Int32 => ArrayPool<int>.Shared.Rent(size),
            FieldType.Float64 => ArrayPool<double>.Shared.Rent(size),
            FieldType.DateTimeUtc => ArrayPool<DateTime>.Shared.Rent(size),
            FieldType.Geometry => ArrayPool<Geometry>.Shared.Rent(size),
            FieldType.Utf8String => new BinaryStringContainer[] {
                new() {
                    Offsets = ArrayPool<int>.Shared.Rent(size + 1),
                    Data = ArrayPool<byte>.Shared.Rent(size * 255) // Schatting: gem. 255 bytes per string
                }
            },
            _ => ArrayPool<string>.Shared.Rent(size)
        };
    }

    private static void ReturnArrayForType(FieldType type, Array array)
    {
        if (array is bool[] b) ArrayPool<bool>.Shared.Return(b);
        else if (array is long[] l) ArrayPool<long>.Shared.Return(l);
        else if (array is int[] i) ArrayPool<int>.Shared.Return(i);
        else if (array is double[] d) ArrayPool<double>.Shared.Return(d);
        else if (array is DateTime[] dt) ArrayPool<DateTime>.Shared.Return(dt);
        else if (array is Geometry[] g) ArrayPool<Geometry>.Shared.Return(g);
        else if (array is string[] s) ArrayPool<string>.Shared.Return(s);
    }

    private static async Task ReadColumnValueAsync(NpgsqlBinaryExporter reader, FieldType type, string pgType, Array buffer, int index, CancellationToken ct)
    {
        switch (type)
        {
            case FieldType.Boolean:
                ((bool[])buffer)[index] = await reader.ReadAsync<bool>(ct);
                break;
            case FieldType.Int64:
                ((long[])buffer)[index] = await reader.ReadAsync<long>(ct);
                break;
            case FieldType.Int32:
                ((int[])buffer)[index] = await reader.ReadAsync<int>(ct);
                break;
            case FieldType.Float64:
                if (pgType == "numeric" || pgType == "decimal")
                {
                    var decVal = await reader.ReadAsync<decimal>(ct);
                    ((double[])buffer)[index] = (double)decVal;
                }
                else if (pgType == "real" || pgType == "float4")
                {
                    var floatVal = await reader.ReadAsync<float>(ct);
                    ((double[])buffer)[index] = floatVal;
                }
                else
                {
                    ((double[])buffer)[index] = await reader.ReadAsync<double>(ct);
                }
                break;
            case FieldType.DateTimeUtc:
                ((DateTime[])buffer)[index] = await reader.ReadAsync<DateTime>(ct);
                break;
            case FieldType.Geometry:
                // Npgsql handelt de binaire EWKB-conversie naar NTS Geometry nu direct intern af dankzij UseNetTopologySuite()
                ((Geometry[])buffer)[index] = await reader.ReadAsync<Geometry>(ct);
                break;
            default: // Dit is FieldType.Utf8String
                var container = ((BinaryStringContainer[])buffer)[0];
                // Lees het veld als rauwe byte-array (Postgres stuurt dit over de lijn als UTF-8)
                var rawBytes = await reader.ReadAsync<byte[]>(ct);

                // Zorg dat de data-buffer groot genoeg is (indien nodig resizen)
                if (container.CurrentOffset + rawBytes.Length > container.Data.Length)
                {
                    var newData = ArrayPool<byte>.Shared.Rent((container.Data.Length + rawBytes.Length) * 2);
                    Array.Copy(container.Data, newData, container.CurrentOffset);
                    ArrayPool<byte>.Shared.Return(container.Data);
                    container.Data = newData;
                }

                // Kopieer data en zet de offset
                Array.Copy(rawBytes, 0, container.Data, container.CurrentOffset, rawBytes.Length);
                container.Offsets[index] = container.CurrentOffset;
                container.CurrentOffset += rawBytes.Length;
                container.Offsets[index + 1] = container.CurrentOffset; // Volgende beginpunt
                break;
        }
    }

    private static IRecordBatch BuildBatch(Schema schema, Array[] buffers, bool[][] nullBitmaps, int rowCount)
    {
        var batch = new GeneratedRecordBatch(schema, rowCount);
        for (int i = 0; i < buffers.Length; i++)
        {
            // Geef de null-bitmap mee aan de batch zodat downstream blokken nulls kunnen detecteren.
            // Geometrie-kolommen hebben geen bitmap nodig: nulls zijn null-referenties in de array zelf.
            bool[] bitmap = nullBitmaps[i];
            if (buffers[i] is bool[] b) batch.AddPrimitiveColumn(i, b, rowCount, bitmap);
            else if (buffers[i] is long[] l) batch.AddPrimitiveColumn(i, l, rowCount, bitmap);
            else if (buffers[i] is int[] intArr) batch.AddPrimitiveColumn(i, intArr, rowCount, bitmap);
            else if (buffers[i] is double[] d) batch.AddPrimitiveColumn(i, d, rowCount, bitmap);
            else if (buffers[i] is DateTime[] dt) batch.AddPrimitiveColumn(i, dt, rowCount, bitmap);
            else if (buffers[i] is Geometry[] g)
            {
                batch.AddGeometryColumn(i, g, rowCount);
                // Geometrie gebruikt null-referenties; de bitmap is niet nodig maar moet nog wel gepoold worden
                ArrayPool<bool>.Shared.Return(bitmap);
            }
            else if (buffers[i] is BinaryStringContainer[] sc)
            {
                var container = sc[0];
                batch.AddStringColumn(i, container.Offsets, container.Data, rowCount, bitmap);
            }
        }
        return batch;
    }
}