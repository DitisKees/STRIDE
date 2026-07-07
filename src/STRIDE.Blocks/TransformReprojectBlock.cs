using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("TransformReproject")]
public sealed class TransformReprojectBlock(string sourceCrs, string targetCrs) : ITransformBlock
{
    private readonly int _sourceSrid = ParseSrid(sourceCrs);
    private readonly int _targetSrid = ParseSrid(targetCrs);
    private readonly GeometryFactory _outputGeometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: ParseSrid(targetCrs));

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        var schema = inputSchemas["in"];
        if (schema.GeometryFieldIndex < 0)
        {
            throw new InvalidOperationException("TransformReproject requires a geometry field in the input schema.");
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

                yield return ReprojectBatch(batch);
            }
        }
    }

    private RecordBatch ReprojectBatch(IRecordBatch batch)
    {
        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformReproject requires a geometry field in the input schema.");
        }

        var geometries = batch.GeometryColumn(geometryOrdinal).Values;
        var transformed = new Geometry?[batch.RowCount];

        for (var row = 0; row < batch.RowCount; row++)
        {
            transformed[row] = geometries[row] is Geometry geometry
                ? ReprojectGeometry(geometry)
                : null;
        }

        var columns = new object?[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            columns[col] = col == geometryOrdinal
                ? new GeometryColumn(transformed)
                : CopyColumn(batch, col);
        }

        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }

    private Geometry ReprojectGeometry(Geometry geometry)
    {
        var copy = (Geometry)geometry.Copy();
        copy.Apply(new ReprojectSequenceFilter(_sourceSrid, _targetSrid));
        copy.GeometryChanged();
        return _outputGeometryFactory.CreateGeometry(copy);
    }

    private static int ParseSrid(string crs)
    {
        var parts = crs.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("EPSG", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var srid))
        {
            throw new InvalidOperationException($"CRS '{crs}' must be in 'EPSG:####' format.");
        }

        return srid;
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

    private sealed class ReprojectSequenceFilter : ICoordinateSequenceFilter
    {
        private readonly int _sourceSrid;
        private readonly int _targetSrid;

        public ReprojectSequenceFilter(int sourceSrid, int targetSrid)
        {
            _sourceSrid = sourceSrid;
            _targetSrid = targetSrid;
        }

        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int i)
        {
            var x = sequence.GetX(i);
            var y = sequence.GetY(i);

            if (_sourceSrid == _targetSrid)
            {
                return;
            }

            if (_sourceSrid == 4326 && _targetSrid == 3857)
            {
                var transformedX = x * 20037508.34 / 180.0;
                var transformedY = Math.Log(Math.Tan((90.0 + y) * Math.PI / 360.0)) / (Math.PI / 180.0);
                transformedY = transformedY * 20037508.34 / 180.0;
                sequence.SetX(i, transformedX);
                sequence.SetY(i, transformedY);
                return;
            }

            if (_sourceSrid == 3857 && _targetSrid == 4326)
            {
                var transformedX = x / 20037508.34 * 180.0;
                var transformedY = y / 20037508.34 * 180.0;
                transformedY = 180.0 / Math.PI * (2.0 * Math.Atan(Math.Exp(transformedY * Math.PI / 180.0)) - Math.PI / 2.0);
                sequence.SetX(i, transformedX);
                sequence.SetY(i, transformedY);
                return;
            }

            throw new InvalidOperationException($"TransformReproject currently supports EPSG:4326<->EPSG:3857 and identity transforms. Got EPSG:{_sourceSrid} -> EPSG:{_targetSrid}.");
        }
    }
}
