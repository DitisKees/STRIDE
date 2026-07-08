using STRIDE.Abstractions;
using System.Collections.Immutable;

namespace STRIDE.Blocks;

[StrideBlock("SourceDataGenerator")]
public sealed class SourceDataGenerator(
    int maxRows = 100_000,
    int batchSize = 4096,
    int rowsPerSecond = 0,
    int seed = 42) : ISourceBlock
{
    private static readonly Schema s_schema = new(ImmutableArray.Create(
        new FieldDef("id", FieldType.Int64, Nullable: false),
        new FieldDef("longitude", FieldType.Float64, Nullable: false),
        new FieldDef("latitude", FieldType.Float64, Nullable: false)));

    private readonly int _maxRows = Math.Max(0, maxRows);
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly int _rowsPerSecond = Math.Max(0, rowsPerSecond);

    public Schema DeriveOutputSchema()
        => s_schema;

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = context;

        var random = new Random(seed);
        var rowsEmitted = 0;

        while (rowsEmitted < _maxRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentBatchSize = Math.Min(_batchSize, _maxRows - rowsEmitted);
            if (currentBatchSize <= 0)
            {
                break;
            }

            var ids = new long[currentBatchSize];
            var longitudes = new double[currentBatchSize];
            var latitudes = new double[currentBatchSize];

            for (var i = 0; i < currentBatchSize; i++)
            {
                ids[i] = rowsEmitted + i;
                longitudes[i] = 6.4 + (random.NextDouble() * 0.2);
                latitudes[i] = 52.2 + (random.NextDouble() * 0.2);
            }

            rowsEmitted += currentBatchSize;

            yield return new RecordBatch(
                s_schema,
                currentBatchSize,
                [ids, longitudes, latitudes]);

            if (_rowsPerSecond > 0 && rowsEmitted < _maxRows)
            {
                var delay = TimeSpan.FromSeconds((double)currentBatchSize / _rowsPerSecond);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}