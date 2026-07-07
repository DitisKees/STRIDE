using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformBuffer")]
public sealed class TransformBufferBlock(double distance) : ITransformBlock
{
    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformBuffer requires a geometry field in the input schema.");
        }

        return schema;
    }

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
                if (batch.RowCount == 0)
                {
                    continue;
                }

                yield return BufferBatch(batch);
            }
        }
    }

    private RecordBatch BufferBatch(IRecordBatch batch)
    {
        if (batch is not RecordBatch input)
        {
            var rows = new string[batch.RowCount][];
            for (var row = 0; row < batch.RowCount; row++)
            {
                rows[row] = new string[batch.Schema.Fields.Length];
                for (var col = 0; col < batch.Schema.Fields.Length; col++)
                {
                    rows[row][col] = batch.GetValueAsString(col, row);
                }
            }

            input = RecordBatch.FromRows(batch.Schema, rows);
        }

        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformBuffer requires a geometry field in the input schema.");
        }

        var geometryValues = batch.GeometryColumn(geometryOrdinal).Values;
        var buffered = new Geometry?[batch.RowCount];
        for (var row = 0; row < batch.RowCount; row++)
        {
            buffered[row] = geometryValues[row] is Geometry geometry
                ? BufferOp.Buffer(geometry, distance)
                : null;
        }

        var columns = new object?[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            if (col == geometryOrdinal)
            {
                columns[col] = new GeometryColumn(buffered);
                continue;
            }

            columns[col] = CopyColumn(batch, col);
        }

        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }

    private static object CopyColumn(IRecordBatch batch, int ordinal)
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

    private static Utf8StringColumn CopyStringColumn(IRecordBatch batch, int ordinal)
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
