using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using STRIDE.Abstractions;
using System.Collections.Immutable;

namespace STRIDE.Blocks;

[StrideBlock("TransformLineMerger")]
public sealed class TransformLineMergerBlock : ITransformBlock
{
    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => new(ImmutableArray.Create(new FieldDef("geom", FieldType.Geometry, true)));

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        var merger = new LineMerger();
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                var geomOrdinal = batch.Schema.GeometryFieldIndex;
                if (geomOrdinal < 0)
                {
                    throw new InvalidOperationException("TransformLineMerger requires a geometry field.");
                }

                var geometries = batch.GeometryColumn(geomOrdinal).Values;
                foreach (var geometry in geometries)
                {
                    if (geometry is not null)
                    {
                        merger.Add(geometry);
                    }
                }
            }
        }

        var merged = merger.GetMergedLineStrings().Cast<Geometry>().ToArray();
        if (merged.Length == 0)
        {
            yield break;
        }

        var schema = DeriveOutputSchema(new Dictionary<string, Schema>());
        var rows = new string[merged.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = [string.Empty];
        }

        var batchOut = RecordBatch.FromRows(schema, rows);
        var columns = BatchTransformUtilities.CopyColumns(batchOut);
        columns[0] = new GeometryColumn(merged);
        yield return new RecordBatch(schema, merged.Length, columns);
    }
}
