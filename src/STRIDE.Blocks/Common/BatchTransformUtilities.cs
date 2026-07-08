using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Threading.Channels;

namespace STRIDE.Blocks;

internal static class BatchTransformUtilities
{
    public static int ResolveMaxDegreeOfParallelism(BlockParams parameters)
    {
        var configured = parameters.GetOptionalInt32("maxDegreeOfParallelism") ?? Environment.ProcessorCount;
        return Math.Max(1, configured);
    }

    public static bool ResolvePreserveOrder(BlockParams parameters)
        => parameters.GetOptionalBoolean("preserveOrder") ?? true;

    public static async IAsyncEnumerable<IRecordBatch> TransformBatchesAsync(
        BlockContext context,
        ChannelReader<IRecordBatch> reader,
        Func<IRecordBatch, CancellationToken, RecordBatch> transformBatch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxDegreeOfParallelism = ResolveMaxDegreeOfParallelism(context.Parameters);
        var preserveOrder = ResolvePreserveOrder(context.Parameters);

        if (maxDegreeOfParallelism == 1)
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    if (batch.RowCount == 0)
                    {
                        continue;
                    }

                    yield return transformBatch(batch, cancellationToken);
                }
            }

            yield break;
        }

        var capacity = Math.Max(2, maxDegreeOfParallelism * 2);
        var pendingInputs = Channel.CreateBounded<(long Sequence, IRecordBatch Batch)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        var completedOutputs = Channel.CreateBounded<(long Sequence, RecordBatch Batch)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                var sequence = 0L;
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var batch))
                    {
                        if (batch.RowCount == 0)
                        {
                            continue;
                        }

                        await pendingInputs.Writer.WriteAsync((sequence++, batch), cancellationToken).ConfigureAwait(false);
                    }
                }

                pendingInputs.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                pendingInputs.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var workerTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    pendingInputs.Reader.ReadAllAsync(cancellationToken),
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    },
                    async (item, token) =>
                    {
                        var transformed = transformBatch(item.Batch, token);
                        await completedOutputs.Writer.WriteAsync((item.Sequence, transformed), token).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                completedOutputs.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                completedOutputs.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        if (!preserveOrder)
        {
            while (await completedOutputs.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (completedOutputs.Reader.TryRead(out var item))
                {
                    yield return item.Batch;
                }
            }
        }
        else
        {
            var nextSequence = 0L;
            var reorderBuffer = new SortedDictionary<long, RecordBatch>();

            while (await completedOutputs.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (completedOutputs.Reader.TryRead(out var item))
                {
                    if (item.Sequence == nextSequence)
                    {
                        yield return item.Batch;
                        nextSequence++;

                        while (reorderBuffer.Remove(nextSequence, out var bufferedBatch))
                        {
                            yield return bufferedBatch;
                            nextSequence++;
                        }

                        continue;
                    }

                    reorderBuffer[item.Sequence] = item.Batch;
                }
            }

            while (reorderBuffer.Remove(nextSequence, out var bufferedBatch))
            {
                yield return bufferedBatch;
                nextSequence++;
            }

            if (reorderBuffer.Count > 0)
            {
                throw new InvalidOperationException("Ordered parallel transform completed with missing sequence numbers.");
            }
        }

        await producerTask.ConfigureAwait(false);
        await workerTask.ConfigureAwait(false);
    }

    public static RecordBatch EnsureRecordBatch(IRecordBatch batch)
    {
        if (batch is RecordBatch recordBatch)
        {
            return recordBatch;
        }

        var rows = new string[batch.RowCount][];
        for (var row = 0; row < batch.RowCount; row++)
        {
            rows[row] = new string[batch.Schema.Fields.Length];
            for (var col = 0; col < batch.Schema.Fields.Length; col++)
            {
                rows[row][col] = batch.GetValueAsString(col, row);
            }
        }

        return RecordBatch.FromRows(batch.Schema, rows);
    }

    public static object?[] CopyColumns(IRecordBatch batch)
    {
        var columns = new object?[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            columns[col] = CopyColumn(batch, col);
        }

        return columns;
    }

    public static object CopyColumn(IRecordBatch batch, int ordinal)
    {
        var type = batch.Schema.Fields[ordinal].Type;
        return type switch
        {
            FieldType.Boolean => batch.Column<bool>(ordinal).ToArray(),
            FieldType.Int32 => batch.Column<int>(ordinal).ToArray(),
            FieldType.Int64 => batch.Column<long>(ordinal).ToArray(),
            FieldType.Float64 => batch.Column<double>(ordinal).ToArray(),
            FieldType.Geometry => new GeometryColumn(batch.GeometryColumn(ordinal).Values.ToArray()),
            _ => CopyStringColumn(batch, ordinal),
        };
    }

    public static Utf8StringColumn CopyStringColumn(IRecordBatch batch, int ordinal)
    {
        var source = batch.StringColumn(ordinal);
        var values = new string?[batch.RowCount];
        for (var row = 0; row < batch.RowCount; row++)
        {
            values[row] = source.GetString(row);
        }

        return RecordBatch.CreateUtf8Column(values);
    }
}
