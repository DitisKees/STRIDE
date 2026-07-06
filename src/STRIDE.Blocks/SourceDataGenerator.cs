using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using STRIDE.Abstractions;
using STRIDE.Blocks.Models;

namespace STRIDE.Blocks;

[StrideBlock("SourceDataGenerator")]
public sealed class SourceDataGenerator : ISourceBlock
{
    private readonly int _maxRows;
    private readonly int _batchSize;
    private readonly int _rowsPerSecond;

    public SourceDataGenerator(Dictionary<string, string> parameters)
    {
        // Defensieve parsing: haal eventuele spaties of aanhalingstekens weg
        _maxRows = parameters.TryGetValue("maxRows", out var m) && int.TryParse(m.Trim('"'), out var parsedM) ? parsedM : 100_000;
        _batchSize = parameters.TryGetValue("batchSize", out var b) && int.TryParse(b.Trim('"'), out var parsedB) ? parsedB : 4096;
        _rowsPerSecond = parameters.TryGetValue("rowsPerSecond", out var r) && int.TryParse(r.Trim('"'), out var parsedR) ? parsedR : 0;

        // Harde grens om oneindige lussen door batchSize = 0 te voorkomen
        if (_batchSize <= 0) _batchSize = 4096;
    }

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var fields = ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, Nullable: false),
            new FieldDef("longitude", FieldType.Float64, Nullable: false),
            new FieldDef("latitude", FieldType.Float64, Nullable: false)
        );
        return new Schema(fields);
    }

    public async IAsyncEnumerable<IRecordBatch> StreamAsync(BlockContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        var schema = DeriveOutputSchema(ImmutableDictionary<string, Schema>.Empty);
        int rowsEmitted = 0;
        var rand = new Random(42);

        double msPerBatch = 0;
        if (_rowsPerSecond > 0)
        {
            double batchesPerSecond = (double)_rowsPerSecond / _batchSize;
            msPerBatch = 1000.0 / batchesPerSecond;
        }

        Console.WriteLine($"[SourceDataGenerator] Start met genereren van {_maxRows} rijen (Batchgrootte: {_batchSize}).");

        while (rowsEmitted < _maxRows)
        {
            // Mocht er via de token geannuleerd worden, breek dan direct uit de lus
            if (ct.IsCancellationRequested) yield break;

            int currentBatchSize = Math.Min(_batchSize, _maxRows - rowsEmitted);
            if (currentBatchSize <= 0) break; // Veiligheidsanker

            long[] idArray = ArrayPool<long>.Shared.Rent(currentBatchSize);
            double[] lonArray = ArrayPool<double>.Shared.Rent(currentBatchSize);
            double[] latArray = ArrayPool<double>.Shared.Rent(currentBatchSize);

            for (int i = 0; i < currentBatchSize; i++)
            {
                idArray[i] = rowsEmitted + i;
                lonArray[i] = 6.4 + (rand.NextDouble() * 0.2);
                latArray[i] = 52.2 + (rand.NextDouble() * 0.2);
            }

            var batch = new GeneratedRecordBatch(schema, currentBatchSize);
            batch.AddPrimitiveColumn(0, idArray, currentBatchSize);
            batch.AddPrimitiveColumn(1, lonArray, currentBatchSize);
            batch.AddPrimitiveColumn(2, latArray, currentBatchSize);

            rowsEmitted += currentBatchSize;

            // Debug log om te zien of de generator loopt
            if (rowsEmitted % 20480 == 0 || rowsEmitted >= _maxRows)
            {
                Console.WriteLine($"[SourceDataGenerator] Voortgang: {rowsEmitted}/{_maxRows} rijen gegenereerd.");
            }

            yield return batch;

            if (_rowsPerSecond > 0 && msPerBatch > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(msPerBatch), ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }

        Console.WriteLine("[SourceDataGenerator] Generatie voltooid. Generator sluit stroom.");
    }
}