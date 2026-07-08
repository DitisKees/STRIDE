using DotSpatial.Projections;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace STRIDE.Blocks;

[StrideBlock("TransformReproject")]
public sealed class TransformReprojectBlock : ITransformBlock
{
    private static readonly object s_gridInitializationLock = new();
    private static readonly HashSet<string> s_initializedGridDirectories = new(StringComparer.OrdinalIgnoreCase);

    private readonly int _sourceSrid;
    private readonly int _targetSrid;
    private readonly ProjectionInfo _sourceProjection;
    private readonly ProjectionInfo _targetProjection;
    private readonly GeometryFactory _outputGeometryFactory;

    public TransformReprojectBlock(string sourceCrs, string targetCrs, string? gridShiftDirectory = null)
    {
        _sourceSrid = ParseSrid(sourceCrs);
        _targetSrid = ParseSrid(targetCrs);
        _sourceProjection = CreateProjection(_sourceSrid, sourceCrs);
        _targetProjection = CreateProjection(_targetSrid, targetCrs);
        _outputGeometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: _targetSrid);

        GridShift.ThrowGridShiftMissingExceptions = true;
        ValidateRequiredGridShiftTables(gridShiftDirectory);
        InitializeGridShifts(gridShiftDirectory);

        // Validate transform availability during workflow validation.
        _ = TryCreateValidationTransform();
    }

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

        await foreach (var transformed in BatchTransformUtilities.TransformBatchesAsync(
            context,
            reader,
            (batch, token) => ReprojectBatch(batch, context, token),
            cancellationToken).ConfigureAwait(false))
        {
            yield return transformed;
        }
    }

    private RecordBatch ReprojectBatch(IRecordBatch batch, BlockContext context, CancellationToken cancellationToken)
    {
        var geometryOrdinal = batch.Schema.GeometryFieldIndex;
        if (geometryOrdinal < 0)
        {
            throw new InvalidOperationException("TransformReproject requires a geometry field in the input schema.");
        }

        var geometries = batch.GeometryColumn(geometryOrdinal).Values;
        var transformed = new Geometry?[batch.RowCount];

        var maxDegreeOfParallelism = ResolveMaxDegreeOfParallelismPerBatch(context.Parameters);

        if (maxDegreeOfParallelism == 1)
        {
            for (var row = 0; row < batch.RowCount; row++)
            {
                transformed[row] = geometries[row] is Geometry geometry
                    ? ReprojectGeometry(geometry)
                    : null;
            }
        }
        else
        {
            Parallel.ForEach(
                Partitioner.Create(0, batch.RowCount),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                },
                range =>
                {
                    for (var row = range.Item1; row < range.Item2; row++)
                    {
                        transformed[row] = geometries[row] is Geometry geometry
                            ? ReprojectGeometry(geometry)
                            : null;
                    }
                });
        }

        var columns = new object?[batch.Schema.Fields.Length];
        for (var col = 0; col < batch.Schema.Fields.Length; col++)
        {
            columns[col] = col == geometryOrdinal
                ? new GeometryColumn(transformed)
                : BatchTransformUtilities.CopyColumn(batch, col);
        }

        return new RecordBatch(batch.Schema, batch.RowCount, columns);
    }

    private Geometry ReprojectGeometry(Geometry geometry)
    {
        var copy = (Geometry)geometry.Copy();
        if (_sourceSrid != _targetSrid)
        {
            copy.Apply(new ReprojectSequenceFilter(_sourceProjection, _targetProjection));
            copy.GeometryChanged();
        }

        copy.SRID = _targetSrid;
        return _outputGeometryFactory.CreateGeometry(copy);
    }

    private static int ResolveMaxDegreeOfParallelismPerBatch(BlockParams parameters)
    {
        var configured = parameters.GetOptionalInt32("maxDegreeOfParallelismPerBatch") ?? 1;
        return Math.Max(1, configured);
    }

    private static int ParseSrid(string crs)
    {
        if (int.TryParse(crs, out var directSrid))
        {
            return directSrid;
        }

        var parts = crs.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && parts[0].Equals("EPSG", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var srid))
        {
            return srid;
        }

        throw new InvalidOperationException($"CRS '{crs}' must be an EPSG identifier like 'EPSG:4326'.");
    }

    private static ProjectionInfo CreateProjection(int srid, string originalInput)
    {
        try
        {
            var projection = ProjectionInfo.FromEpsgCode(srid);
            if (projection is null)
            {
                throw new InvalidOperationException($"CRS '{originalInput}' could not be resolved to EPSG:{srid}.");
            }

            return projection;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"CRS '{originalInput}' could not be resolved to EPSG:{srid}.", ex);
        }
    }

    private static void InitializeGridShifts(string? gridShiftDirectory)
    {
        if (string.IsNullOrWhiteSpace(gridShiftDirectory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(gridShiftDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Grid shift directory '{fullPath}' does not exist.");
        }

        lock (s_gridInitializationLock)
        {
            if (!s_initializedGridDirectories.Add(fullPath))
            {
                return;
            }

            GridShift.InitializeExternalGrids(fullPath, true);
        }
    }

    private void ValidateRequiredGridShiftTables(string? gridShiftDirectory)
    {
        var requiredGridShiftTables = GetRequiredGridShiftTables(_sourceProjection)
            .Concat(GetRequiredGridShiftTables(_targetProjection))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requiredGridShiftTables.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gridShiftDirectory))
        {
            throw new InvalidOperationException(
                $"Reprojection EPSG:{_sourceSrid} -> EPSG:{_targetSrid} requires grid shift tables ({string.Join(", ", requiredGridShiftTables)}), but no 'gridShiftDirectory' was provided.");
        }

        var fullPath = Path.GetFullPath(gridShiftDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Grid shift directory '{fullPath}' does not exist.");
        }

        foreach (var requiredTable in requiredGridShiftTables)
        {
            if (!GridShiftTableExists(fullPath, requiredTable))
            {
                throw new InvalidOperationException(
                    $"Grid shift table '{requiredTable}' required for EPSG:{_sourceSrid} -> EPSG:{_targetSrid} was not found under '{fullPath}'.");
            }
        }
    }

    private static IEnumerable<string> GetRequiredGridShiftTables(ProjectionInfo projection)
    {
        var datum = projection.GeographicInfo?.Datum;
        if (datum?.NadGrids is null)
        {
            return Enumerable.Empty<string>();
        }

        return datum.NadGrids
            .Where(static table => !string.IsNullOrWhiteSpace(table))
            .Select(static table => table.Trim())
            .Where(static table => !string.Equals(table, "null", StringComparison.OrdinalIgnoreCase));
    }

    private static bool GridShiftTableExists(string rootDirectory, string requiredTable)
    {
        var normalized = requiredTable.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var hasExtension = Path.HasExtension(normalized);
        var candidates = hasExtension
            ? new[] { normalized }
            : new[]
            {
                $"{normalized}.gsb",
                $"{normalized}.dat",
                $"{normalized}.lla",
                $"{normalized}.los",
            };

        foreach (var candidate in candidates)
        {
            if (Directory.EnumerateFiles(rootDirectory, candidate, SearchOption.AllDirectories).Any())
            {
                return true;
            }
        }

        return false;
    }

    private bool TryCreateValidationTransform()
    {
        if (_sourceSrid == _targetSrid)
        {
            return true;
        }

        var xy = new[] { 0d, 0d };
        var z = new[] { 0d };
        try
        {
            Reproject.ReprojectPoints(xy, z, _sourceProjection, _targetProjection, 0, 1);
            return true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to initialize reprojection between EPSG:{_sourceSrid} and EPSG:{_targetSrid}. {ex.Message}",
                ex);
        }
    }

    private sealed class ReprojectSequenceFilter : ICoordinateSequenceFilter
    {
        private readonly ProjectionInfo _sourceProjection;
        private readonly ProjectionInfo _targetProjection;

        public ReprojectSequenceFilter(ProjectionInfo sourceProjection, ProjectionInfo targetProjection)
        {
            _sourceProjection = sourceProjection;
            _targetProjection = targetProjection;
        }

        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int i)
        {
            var x = sequence.GetX(i);
            var y = sequence.GetY(i);
            var z = sequence.HasZ ? sequence.GetZ(i) : 0d;
            var xy = new[] { x, y };
            var zValues = new[] { z };

            Reproject.ReprojectPoints(xy, zValues, _sourceProjection, _targetProjection, 0, 1);

            sequence.SetX(i, xy[0]);
            sequence.SetY(i, xy[1]);

            if (sequence.HasZ)
            {
                var projectedZ = zValues[0];
                if (!double.IsNaN(projectedZ))
                {
                    sequence.SetZ(i, projectedZ);
                }
            }
        }
    }
}
