using STRIDE.Abstractions;
using System.Collections.Immutable;
using Npgsql;
using System.Text;
using System.Text.Json;

namespace STRIDE.Blocks;

[StrideBlock("SourceGml")]
public sealed class SourceGmlBlock(string path) : ISourceBlock
{
    public Schema DeriveOutputSchema()
        => new(ImmutableArray.Create(new FieldDef("xml", FieldType.Utf8String, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var schema = DeriveOutputSchema();
        var rows = lines.Select(static line => new[] { line }).ToArray();
        if (rows.Length > 0)
        {
            yield return RecordBatch.FromRows(schema, rows);
        }
    }
}

[StrideBlock("SourceShapefile")]
public sealed class SourceShapefileBlock(string path) : ISourceBlock
{
    public Schema DeriveOutputSchema()
        => new(ImmutableArray.Create(new FieldDef("path", FieldType.Utf8String, true), new FieldDef("sizeBytes", FieldType.Int64, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var rows = new[] { new[] { path, info.Exists ? info.Length.ToString() : "0" } };
        yield return RecordBatch.FromRows(DeriveOutputSchema(), rows);
        await Task.Yield();
    }
}

[StrideBlock("SourceGeoPackage")]
public sealed class SourceGeoPackageBlock(string path, string? table = null) : ISourceBlock
{
    public Schema DeriveOutputSchema()
        => new(ImmutableArray.Create(new FieldDef("path", FieldType.Utf8String, true), new FieldDef("table", FieldType.Utf8String, true), new FieldDef("sizeBytes", FieldType.Int64, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var rows = new[] { new[] { path, table ?? string.Empty, info.Exists ? info.Length.ToString() : "0" } };
        yield return RecordBatch.FromRows(DeriveOutputSchema(), rows);
        await Task.Yield();
    }
}

[StrideBlock("SourceExcel")]
public sealed class SourceExcelBlock(string path) : ISourceBlock
{
    public Schema DeriveOutputSchema()
        => new(ImmutableArray.Create(new FieldDef("path", FieldType.Utf8String, true), new FieldDef("sizeBytes", FieldType.Int64, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var rows = new[] { new[] { path, info.Exists ? info.Length.ToString() : "0" } };
        yield return RecordBatch.FromRows(DeriveOutputSchema(), rows);
        await Task.Yield();
    }
}

[StrideBlock("SourceWfs")]
public sealed class SourceWfsBlock(string url) : ISourceBlock
{
    public Schema DeriveOutputSchema()
        => new(ImmutableArray.Create(new FieldDef("payload", FieldType.Utf8String, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var payload = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        yield return RecordBatch.FromRows(DeriveOutputSchema(), [new[] { payload }]);
    }
}

[StrideBlock("SinkPostGis")]
public sealed class SinkPostGisBlock(string connectionString, string table) : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var writeMode = context.Parameters.GetOptionalString("writeMode") ?? "Transactional";
        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writeMode.Equals("Transactional", StringComparison.OrdinalIgnoreCase)
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        string[]? columnNames = null;
        string? commandText = null;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (columnNames is null)
                {
                    columnNames = batch.Schema.Fields
                        .Where(static field => field.Type != FieldType.Geometry)
                        .Select(static field => field.Name)
                        .ToArray();

                    var quotedColumns = string.Join(", ", columnNames.Select(static name => $"\"{name}\""));
                    var parameters = string.Join(", ", Enumerable.Range(0, columnNames.Length).Select(static i => $"@p{i}"));
                    commandText = $"INSERT INTO \"{table}\" ({quotedColumns}) VALUES ({parameters});";
                }

                for (var row = 0; row < batch.RowCount; row++)
                {
                    await using var command = new NpgsqlCommand(commandText, connection, transaction);
                    for (var i = 0; i < columnNames.Length; i++)
                    {
                        _ = batch.Schema.TryGetOrdinal(columnNames[i], out var ordinal);
                        var fieldType = batch.Schema.Fields[ordinal].Type;
                        object? value = fieldType switch
                        {
                            FieldType.Boolean => batch.Column<bool>(ordinal)[row],
                            FieldType.Int32 => batch.Column<int>(ordinal)[row],
                            FieldType.Int64 => batch.Column<long>(ordinal)[row],
                            FieldType.Float64 => batch.Column<double>(ordinal)[row],
                            _ => batch.GetValueAsString(ordinal, row),
                        };

                        command.Parameters.AddWithValue($"p{i}", value ?? DBNull.Value);
                    }

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

[StrideBlock("SinkExcel")]
public sealed class SinkExcelBlock(string path) : ISinkBlock
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
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        var headerWritten = false;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (!headerWritten)
                {
                    await writer.WriteLineAsync(string.Join('\t', batch.Schema.Fields.Select(static f => f.Name))).ConfigureAwait(false);
                    headerWritten = true;
                }

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var values = new string[batch.Schema.Fields.Length];
                    for (var col = 0; col < values.Length; col++)
                    {
                        values[col] = batch.GetValueAsString(col, row);
                    }

                    await writer.WriteLineAsync(string.Join('\t', values)).ConfigureAwait(false);
                }
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

[StrideBlock("SinkWfsT")]
public sealed class SinkWfsTBlock(string url) : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var records = new List<Dictionary<string, object?>>(capacity: 128);
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                for (var row = 0; row < batch.RowCount; row++)
                {
                    var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (var col = 0; col < batch.Schema.Fields.Length; col++)
                    {
                        var field = batch.Schema.Fields[col];
                        record[field.Name] = batch.GetValueAsString(col, row);
                    }

                    records.Add(record);
                }
            }
        }

        using var httpClient = new HttpClient();
        var payload = JsonSerializer.Serialize(records);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

[StrideBlock("TransformSplitGeometry")]
public sealed class TransformSplitGeometryBlock : ITransformBlock
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
                yield return BatchTransformUtilities.EnsureRecordBatch(batch);
            }
        }
    }
}
