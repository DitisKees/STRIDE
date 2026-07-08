using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("SinkPostGis")]
public sealed class SinkPostGisBlock(string connectionString, string table) : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var writeMode = SinkWriteModeUtilities.Parse(context.Parameters);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();

        await using var dataSource = builder.Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        NpgsqlTransaction? transaction = null;
        string? copySql = null;
        var columnTypes = Array.Empty<FieldType>();
        var rowsWritten = 0L;
        var batchesWritten = 0;

        try
        {
            if (writeMode.IsTransactional)
            {
                transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    if (!writeMode.IsTransactional && transaction is null)
                    {
                        transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (copySql is null)
                    {
                        var columns = batch.Schema.Fields.Select(static field => QuoteIdentifier(field.Name)).ToArray();
                        copySql = $"COPY {QuoteIdentifier(table)} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)";
                        columnTypes = batch.Schema.Fields.Select(static field => field.Type).ToArray();
                    }

                    await using var importer = await connection.BeginBinaryImportAsync(copySql, cancellationToken).ConfigureAwait(false);
                    for (var row = 0; row < batch.RowCount; row++)
                    {
                        await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                        for (var col = 0; col < batch.Schema.Fields.Length; col++)
                        {
                            WriteField(importer, batch, columnTypes[col], col, row);
                        }
                    }

                    await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    rowsWritten += batch.RowCount;
                    batchesWritten++;

                    if (!writeMode.IsTransactional
                        && transaction is not null
                        && batchesWritten % writeMode.BatchCommitInterval == 0)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.DisposeAsync().ConfigureAwait(false);
                        transaction = null;
                    }
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
                transaction = null;
            }
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort rollback.
                }

                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (!writeMode.IsTransactional)
            {
                await context.Errors.WriteRecordErrorAsync(
                    context.NodeId,
                    $"SinkPostGis committed batches up to failure point. rowsWritten={rowsWritten}, batchesWritten={batchesWritten}.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static void WriteField(
        NpgsqlBinaryImporter importer,
        IRecordBatch batch,
        FieldType fieldType,
        int ordinal,
        int row)
    {
        switch (fieldType)
        {
            case FieldType.Boolean:
                importer.Write(batch.Column<bool>(ordinal)[row], NpgsqlDbType.Boolean);
                return;

            case FieldType.Int32:
                importer.Write(batch.Column<int>(ordinal)[row], NpgsqlDbType.Integer);
                return;

            case FieldType.Int64:
                importer.Write(batch.Column<long>(ordinal)[row], NpgsqlDbType.Bigint);
                return;

            case FieldType.Float64:
                importer.Write(batch.Column<double>(ordinal)[row], NpgsqlDbType.Double);
                return;

            case FieldType.Geometry:
                {
                    var geometry = batch.GeometryColumn(ordinal).Values[row];
                    if (geometry is null)
                    {
                        importer.WriteNull();
                    }
                    else
                    {
                        importer.Write(geometry, NpgsqlDbType.Geometry);
                    }

                    return;
                }

            default:
                {
                    var value = batch.GetValueAsString(ordinal, row);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        importer.WriteNull();
                    }
                    else
                    {
                        importer.Write(value, NpgsqlDbType.Text);
                    }

                    return;
                }
        }
    }

    private static string QuoteIdentifier(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}
