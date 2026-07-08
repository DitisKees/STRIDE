using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;

namespace STRIDE.Blocks;

[StrideBlock("TransformCreateGeometry")]
public sealed class TransformCreateGeometryBlock(string xField, string yField, string geometryFieldName = "geom", int srid = 4326) : ITransformBlock
{
    private readonly GeometryFactory _geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: srid);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var input = inputSchemas["in"];
        if (input.TryGetOrdinal(geometryFieldName, out var geometryOrdinal))
        {
            var fields = input.Fields.ToArray();
            fields[geometryOrdinal] = new FieldDef(geometryFieldName, FieldType.Geometry, true);
            return new Schema(fields.ToImmutableArray());
        }

        return new Schema(input.Fields.Add(new FieldDef(geometryFieldName, FieldType.Geometry, true)));
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
                if (!batch.Schema.TryGetOrdinal(xField, out var xOrdinal) || !batch.Schema.TryGetOrdinal(yField, out var yOrdinal))
                {
                    throw new InvalidOperationException($"TransformCreateGeometry requires xField '{xField}' and yField '{yField}' in the input schema.");
                }

                var outputSchema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase) { ["in"] = batch.Schema });
                var geometryOrdinal = outputSchema.TryGetOrdinal(geometryFieldName, out var resolvedOrdinal) ? resolvedOrdinal : outputSchema.GeometryFieldIndex;
                var columns = new object?[outputSchema.Fields.Length];

                for (var col = 0; col < outputSchema.Fields.Length; col++)
                {
                    if (col == geometryOrdinal)
                    {
                        continue;
                    }

                    if (col < batch.Schema.Fields.Length)
                    {
                        columns[col] = BatchTransformUtilities.CopyColumn(batch, col);
                    }
                }

                var geometries = new Geometry?[batch.RowCount];
                for (var row = 0; row < batch.RowCount; row++)
                {
                    if (double.TryParse(batch.GetValueAsString(xOrdinal, row), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var x)
                        && double.TryParse(batch.GetValueAsString(yOrdinal, row), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var y))
                    {
                        geometries[row] = _geometryFactory.CreatePoint(new Coordinate(x, y));
                    }
                }

                columns[geometryOrdinal] = new GeometryColumn(geometries);
                yield return new RecordBatch(outputSchema, batch.RowCount, columns);
            }
        }
    }
}
