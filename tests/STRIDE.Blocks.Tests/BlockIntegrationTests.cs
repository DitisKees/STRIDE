using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using STRIDE.Abstractions;
using STRIDE.Blocks;
using System.Collections.Immutable;
using System.Data.SQLite;
using System.Text.Json;
using System.Threading.Channels;
using Schema = STRIDE.Abstractions.Schema;

namespace STRIDE.Blocks.Tests;

public class BlockIntegrationTests
{
    [Fact]
    public async Task TransformAggregatorAggregatesRowsAndReadsSpilledState()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("category", FieldType.Utf8String, false),
            new FieldDef("value", FieldType.Float64, false)));

        var batch1 = RecordBatch.FromRows(schema,
        [
            ["A", "10"],
            ["B", "7"],
            ["A", "5"],
        ]);

        var batch2 = RecordBatch.FromRows(schema,
        [
            ["B", "3"],
            ["A", "5"],
            ["B", "10"],
        ]);

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch1);
        await input.Writer.WriteAsync(batch2);
        input.Writer.TryComplete();

        var block = new TransformAggregatorBlock(
            groupBy: "category",
            aggregates: "count:*:row_count;sum:value:total_value;avg:value:avg_value;min:value:min_value;max:value:max_value");

        var context = new BlockContext(
            "aggregator",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input.Reader,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            new BlockParams(new Dictionary<string, object?>
            {
                ["spillThresholdBytes"] = 1,
                ["batchSize"] = 10,
            }),
            CancellationToken.None);

        var rows = new List<string[]>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            for (var row = 0; row < output.RowCount; row++)
            {
                var values = new string[output.Schema.Fields.Length];
                for (var col = 0; col < output.Schema.Fields.Length; col++)
                {
                    values[col] = output.GetValueAsString(col, row);
                }

                rows.Add(values);
            }
        }

        Assert.Equal(2, rows.Count);
        var byCategory = rows.ToDictionary(static x => x[0], StringComparer.Ordinal);

        Assert.Equal("3", byCategory["A"][1]);
        Assert.Equal("20", byCategory["A"][2]);
        Assert.Equal("6.666666666666667", byCategory["A"][3]);
        Assert.Equal("5", byCategory["A"][4]);
        Assert.Equal("10", byCategory["A"][5]);

        Assert.Equal("3", byCategory["B"][1]);
        Assert.Equal("20", byCategory["B"][2]);
        Assert.Equal("6.666666666666667", byCategory["B"][3]);
        Assert.Equal("3", byCategory["B"][4]);
        Assert.Equal("10", byCategory["B"][5]);
    }

    [Fact]
    public async Task SourceGeoJsonLoadsFeaturesAndGeometryColumn()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "input.geojson");

        await File.WriteAllTextAsync(inputPath, """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "properties": { "id": 1, "name": "alpha" }, "geometry": { "type": "Point", "coordinates": [5.0, 52.0] } },
            { "type": "Feature", "properties": { "id": 2, "name": "beta" }, "geometry": { "type": "Point", "coordinates": [6.0, 53.0] } }
          ]
        }
        """);

        var source = new SourceGeoJsonBlock(inputPath);
        var schema = source.DeriveOutputSchema();

        Assert.True(schema.GeometryFieldIndex >= 0);
        Assert.True(schema.TryGetOrdinal("id", out _));

        var context = new BlockContext(
            "source",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        var batches = new List<IRecordBatch>();
        await foreach (var batch in source.ExecuteAsync(context, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Single(batches);
        Assert.Equal(2, batches[0].RowCount);
        Assert.NotNull(batches[0].GeometryColumn(schema.GeometryFieldIndex).Values[0]);
    }

    [Fact]
    public async Task SinkExcelWritesHeaderAndRows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.xlsx");

        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("name", FieldType.Utf8String, true)));

        var batch = new RecordBatch(schema, 2, new object?[]
        {
            new long[] { 1, 2 },
            RecordBatch.CreateUtf8Column(new string?[] { "alpha", "beta" }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var sink = new SinkExcelBlock(outputPath);
        await sink.ExecuteAsync(CreateSinkContext(input.Reader), CancellationToken.None);

        using var document = SpreadsheetDocument.Open(outputPath, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Workbook part was not created.");
        var workbook = workbookPart.Workbook ?? throw new InvalidOperationException("Workbook metadata was not created.");
        var firstSheet = workbook.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidOperationException("Workbook did not contain any sheets.");
        var sheetId = firstSheet.Id?.Value ?? throw new InvalidOperationException("Worksheet sheet id is missing.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetId);
        var worksheet = worksheetPart.Worksheet ?? throw new InvalidOperationException("Worksheet was not created.");
        var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList()
            ?? [];

        Assert.Equal(3, rows.Count);
        Assert.Equal("id", ReadExcelCellText(rows[0].Elements<Cell>().First()));
        Assert.Equal("name", ReadExcelCellText(rows[0].Elements<Cell>().ElementAt(1)));
        Assert.Equal("1", ReadExcelCellText(rows[1].Elements<Cell>().First()));
        Assert.Equal("alpha", ReadExcelCellText(rows[1].Elements<Cell>().ElementAt(1)));
        Assert.Equal("2", ReadExcelCellText(rows[2].Elements<Cell>().First()));
        Assert.Equal("beta", ReadExcelCellText(rows[2].Elements<Cell>().ElementAt(1)));
    }

    [Fact]
    public async Task SourceExcelReadsRowsAndParsesGeometry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "source-input.xlsx");

        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Utf8String, true)));

        var batch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 7 },
            RecordBatch.CreateUtf8Column(new string?[] { "POINT (5.1 52.1)" }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var sink = new SinkExcelBlock(outputPath);
        await sink.ExecuteAsync(CreateSinkContext(input.Reader), CancellationToken.None);

        var source = new SourceExcelBlock(outputPath, geometryColumn: "geom");
        var sourceSchema = source.DeriveOutputSchema();
        Assert.True(sourceSchema.GeometryFieldIndex >= 0);

        var context = new BlockContext(
            "source",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in source.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        Assert.Equal(1, outputs[0].RowCount);
        var point = Assert.IsType<Point>(outputs[0].GeometryColumn(sourceSchema.GeometryFieldIndex).Values[0]);
        Assert.InRange(Math.Abs(point.X - 5.1), 0, 1e-9);
        Assert.InRange(Math.Abs(point.Y - 52.1), 0, 1e-9);
    }

    [Fact]
    public async Task SourceGeoPackageReadsRowsAndGeometryFromFeatureTable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var gpkgPath = Path.Combine(tempDir, "input.gpkg");

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var point = geometryFactory.CreatePoint(new Coordinate(4.9, 52.3));
        var wkb = new WKBWriter().Write(point);

        using (var connection = new SQLiteConnection($"Data Source={gpkgPath};Version=3;"))
        {
            connection.Open();

            using var setup = connection.CreateCommand();
            setup.CommandText = """
                CREATE TABLE gpkg_contents (table_name TEXT NOT NULL, data_type TEXT NOT NULL);
                CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL);
                CREATE TABLE features (id INTEGER PRIMARY KEY, name TEXT, geom BLOB);
                INSERT INTO gpkg_contents (table_name, data_type) VALUES ('features', 'features');
                INSERT INTO gpkg_geometry_columns (table_name, column_name) VALUES ('features', 'geom');
                """;
            setup.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO features (id, name, geom) VALUES ($id, $name, $geom);";
            insert.Parameters.AddWithValue("$id", 1);
            insert.Parameters.AddWithValue("$name", "alpha");
            insert.Parameters.AddWithValue("$geom", wkb);
            insert.ExecuteNonQuery();
        }

        var source = new SourceGeoPackageBlock(gpkgPath, table: "features", batchSize: 50, geometryColumn: "geom");
        var schema = source.DeriveOutputSchema();
        Assert.True(schema.GeometryFieldIndex >= 0);
        Assert.True(schema.TryGetOrdinal("id", out var idOrdinal));

        var context = new BlockContext(
            "source",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in source.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        Assert.Equal(1, outputs[0].RowCount);
        Assert.Equal("1", outputs[0].GetValueAsString(idOrdinal, 0));

        var outputPoint = Assert.IsType<Point>(outputs[0].GeometryColumn(schema.GeometryFieldIndex).Values[0]);
        Assert.InRange(Math.Abs(outputPoint.X - point.X), 0, 1e-9);
        Assert.InRange(Math.Abs(outputPoint.Y - point.Y), 0, 1e-9);
    }

    [Fact]
    public async Task SourceCheckpointUtilitiesRoundTripTypedTokens()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var checkpointPath = Path.Combine(tempDir, "checkpoint.json");

        var token = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        await SourceCheckpointUtilities.WriteTokenAsync(checkpointPath, token, CancellationToken.None);

        var restored = await SourceCheckpointUtilities.ReadTokenAsync(checkpointPath, CancellationToken.None);
        var restoredValue = Assert.IsType<DateTimeOffset>(restored);
        Assert.Equal(token, restoredValue);

        await SourceCheckpointUtilities.WriteTokenAsync(checkpointPath, 42L, CancellationToken.None);
        var restoredInt = await SourceCheckpointUtilities.ReadTokenAsync(checkpointPath, CancellationToken.None);
        Assert.Equal(42L, Assert.IsType<long>(restoredInt));
    }

    [Fact]
    public async Task TransformBufferProducesBufferedGeometries()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var batch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 1 },
            new GeometryColumn(new Geometry?[] { geometryFactory.CreatePoint(new Coordinate(5, 52)) }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var block = new TransformBufferBlock(0.01);
        var context = CreateTransformContext(input.Reader);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        var outputGeom = outputs[0].GeometryColumn(schema.GeometryFieldIndex).Values[0];
        Assert.NotNull(outputGeom);
        Assert.Equal("Polygon", outputGeom!.GeometryType);
    }

    [Fact]
    public async Task TransformBufferPreservesInputBatchOrderWhenParallelized()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var input = Channel.CreateUnbounded<IRecordBatch>();

        const int rowCount = 64;
        for (var id = 0; id < rowCount; id++)
        {
            var batch = new RecordBatch(schema, 1, new object?[]
            {
                new long[] { id },
                new GeometryColumn(new Geometry?[]
                {
                    geometryFactory.CreatePoint(new Coordinate(4.5 + (id * 0.001), 52.0)),
                }),
            });

            await input.Writer.WriteAsync(batch);
        }

        input.Writer.TryComplete();

        var context = CreateTransformContext(
            input.Reader,
            new BlockParams(new Dictionary<string, object?>
            {
                ["maxDegreeOfParallelism"] = 4,
                ["preserveOrder"] = true,
            }));

        var block = new TransformBufferBlock(0.001);
        var outputIds = new List<long>();

        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputIds.AddRange(output.Column<long>(0).ToArray());
        }

        Assert.Equal(rowCount, outputIds.Count);
        for (var i = 0; i < rowCount; i++)
        {
            Assert.Equal(i, outputIds[i]);
        }
    }

    [Fact]
    public async Task TransformReprojectTransformsCoordinatesBetween4326And3857()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var batch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 1 },
            new GeometryColumn(new Geometry?[] { geometryFactory.CreatePoint(new Coordinate(1, 1)) }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var block = new TransformReprojectBlock("EPSG:4326", "EPSG:3857");
        var context = CreateTransformContext(input.Reader);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        var point = Assert.IsType<Point>(outputs[0].GeometryColumn(schema.GeometryFieldIndex).Values[0]);
        Assert.True(Math.Abs(point.X) > 100000);
        Assert.True(Math.Abs(point.Y) > 100000);
    }

    [Fact]
    public async Task TransformReprojectRoundTripsBetween4326And3857()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var original = geometryFactory.CreatePoint(new Coordinate(4.895168, 52.370216));
        var batch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 1 },
            new GeometryColumn(new Geometry?[] { original }),
        });

        var inputA = Channel.CreateUnbounded<IRecordBatch>();
        await inputA.Writer.WriteAsync(batch);
        inputA.Writer.TryComplete();

        var forward = new TransformReprojectBlock("EPSG:4326", "EPSG:3857");
        var forwardContext = CreateTransformContext(inputA.Reader);

        var projected = new List<IRecordBatch>();
        await foreach (var output in forward.ExecuteAsync(forwardContext, CancellationToken.None))
        {
            projected.Add(output);
        }

        var inputB = Channel.CreateUnbounded<IRecordBatch>();
        await inputB.Writer.WriteAsync(projected[0]);
        inputB.Writer.TryComplete();

        var reverse = new TransformReprojectBlock("EPSG:3857", "EPSG:4326");
        var reverseContext = CreateTransformContext(inputB.Reader);

        var restored = new List<IRecordBatch>();
        await foreach (var output in reverse.ExecuteAsync(reverseContext, CancellationToken.None))
        {
            restored.Add(output);
        }

        var roundTripped = Assert.IsType<Point>(restored[0].GeometryColumn(schema.GeometryFieldIndex).Values[0]);
        Assert.InRange(Math.Abs(roundTripped.X - original.X), 0, 1e-6);
        Assert.InRange(Math.Abs(roundTripped.Y - original.Y), 0, 1e-6);
    }

    [Fact]
    public void TransformReprojectThrowsOnUnknownEpsgCode()
    {
        Assert.ThrowsAny<Exception>(() => new TransformReprojectBlock("EPSG:999999", "EPSG:4326"));
    }

    [Fact]
    public async Task TransformSplitGeometrySplitsPolygonIntoMultipleRows()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var polygon = geometryFactory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(10, 0),
            new Coordinate(10, 10),
            new Coordinate(0, 10),
            new Coordinate(0, 0),
        ]);

        var batch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 42 },
            new GeometryColumn(new Geometry?[] { polygon }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var block = new TransformSplitGeometryBlock("LINESTRING (5 -1, 5 11)");
        var context = CreateTransformContext(input.Reader);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        Assert.Equal(2, outputs[0].RowCount);
        Assert.Equal(42L, outputs[0].Column<long>(0)[0]);
        Assert.Equal(42L, outputs[0].Column<long>(0)[1]);

        var left = Assert.IsType<Polygon>(outputs[0].GeometryColumn(schema.GeometryFieldIndex).Values[0]);
        var right = Assert.IsType<Polygon>(outputs[0].GeometryColumn(schema.GeometryFieldIndex).Values[1]);
        Assert.InRange(Math.Abs((left.Area + right.Area) - polygon.Area), 0, 1e-6);
    }

    [Fact]
    public async Task TransformSpatialJoinFiltersInputByLookupGeometry()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var inputBatch = new RecordBatch(schema, 2, new object?[]
        {
            new long[] { 1, 2 },
            new GeometryColumn(new Geometry?[]
            {
                geometryFactory.CreatePoint(new Coordinate(5, 52)),
                geometryFactory.CreatePoint(new Coordinate(50, 50)),
            }),
        });

        var lookupPolygon = geometryFactory.CreatePolygon([
            new Coordinate(4, 51),
            new Coordinate(6, 51),
            new Coordinate(6, 53),
            new Coordinate(4, 53),
            new Coordinate(4, 51),
        ]);

        var lookupBatch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 10 },
            new GeometryColumn(new Geometry?[] { lookupPolygon }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        var lookup = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(inputBatch);
        await lookup.Writer.WriteAsync(lookupBatch);
        input.Writer.TryComplete();
        lookup.Writer.TryComplete();

        var block = new TransformSpatialJoinBlock("Intersects");
        var context = new BlockContext(
            "join",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input.Reader,
                ["lookup"] = lookup.Reader,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        Assert.Equal(1, outputs[0].RowCount);
        Assert.Equal(1L, outputs[0].Column<long>(0)[0]);
    }

    [Fact]
    public async Task TransformSpatialJoinSupportsParallelQueriesAfterIndexBuild()
    {
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var inputGeometries = new Geometry?[40];
        var inputIds = new long[40];
        for (var i = 0; i < inputGeometries.Length; i++)
        {
            inputIds[i] = i + 1;
            inputGeometries[i] = geometryFactory.CreatePoint(new Coordinate(i, i));
        }

        var inputBatch = new RecordBatch(schema, inputGeometries.Length, new object?[]
        {
            inputIds,
            new GeometryColumn(inputGeometries),
        });

        var lookupPolygon = geometryFactory.CreatePolygon([
            new Coordinate(10, 10),
            new Coordinate(30, 10),
            new Coordinate(30, 30),
            new Coordinate(10, 30),
            new Coordinate(10, 10),
        ]);

        var lookupBatch = new RecordBatch(schema, 1, new object?[]
        {
            new long[] { 1 },
            new GeometryColumn(new Geometry?[] { lookupPolygon }),
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        var lookup = Channel.CreateUnbounded<IRecordBatch>();
        await input.Writer.WriteAsync(inputBatch);
        await lookup.Writer.WriteAsync(lookupBatch);
        input.Writer.TryComplete();
        lookup.Writer.TryComplete();

        var block = new TransformSpatialJoinBlock("Intersects");
        var context = new BlockContext(
            "join",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input.Reader,
                ["lookup"] = lookup.Reader,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            new BlockParams(new Dictionary<string, object?>
            {
                ["maxDegreeOfParallelism"] = 4,
            }),
            CancellationToken.None);

        var outputs = new List<IRecordBatch>();
        await foreach (var output in block.ExecuteAsync(context, CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Single(outputs);
        Assert.Equal(21, outputs[0].RowCount);
        Assert.Equal(11L, outputs[0].Column<long>(0)[0]);
        Assert.Equal(31L, outputs[0].Column<long>(0)[20]);
    }

    [Fact]
    public async Task TransformConditionalSplitterRoutesRowsToNamedPorts()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("score", FieldType.Float64, false)));

        var batch = new RecordBatch(schema, 3, new object?[]
        {
            new long[] { 1, 2, 3 },
            new double[] { 5, 50, 500 },
        });

        var input = Channel.CreateUnbounded<IRecordBatch>();
        var low = Channel.CreateUnbounded<IRecordBatch>();
        var high = Channel.CreateUnbounded<IRecordBatch>();
        var unmatched = Channel.CreateUnbounded<IRecordBatch>();

        await input.Writer.WriteAsync(batch);
        input.Writer.TryComplete();

        var block = new TransformConditionalSplitterBlock("low:score < 10;high:score >= 100");
        var context = new BlockContext(
            "splitter",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input.Reader,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["low"] = low.Writer,
                ["high"] = high.Writer,
                ["out"] = unmatched.Writer,
            },
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        await foreach (var _ in block.ExecuteAsync(context, CancellationToken.None))
        {
        }

        low.Writer.TryComplete();
        high.Writer.TryComplete();
        unmatched.Writer.TryComplete();

        var lowRows = await ReadRowsAsync(low.Reader);
        var highRows = await ReadRowsAsync(high.Reader);
        var unmatchedRows = await ReadRowsAsync(unmatched.Reader);

        Assert.Single(lowRows);
        Assert.Single(highRows);
        Assert.Single(unmatchedRows);
        Assert.Equal("1", lowRows[0][0]);
        Assert.Equal("3", highRows[0][0]);
        Assert.Equal("2", unmatchedRows[0][0]);
    }

    [Fact]
    public async Task SinkGeoJsonWritesFeatureCollection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.geojson");

        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("name", FieldType.Utf8String, true),
            new FieldDef("geom", FieldType.Geometry, true)));

        var idColumn = new long[] { 1, 2 };
        var nameColumn = RecordBatch.CreateUtf8Column(new string?[] { "a", "b" });
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geomColumn = new GeometryColumn(new NetTopologySuite.Geometries.Geometry?[]
        {
            geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(5, 52)),
            geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(6, 53)),
        });

        var batch = new RecordBatch(schema, 2, new object?[] { idColumn, nameColumn, geomColumn });

        var channel = Channel.CreateUnbounded<IRecordBatch>();
        await channel.Writer.WriteAsync(batch);
        channel.Writer.TryComplete();

        var sink = new SinkGeoJsonBlock(outputPath);
        var context = new BlockContext(
            "sink",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = channel.Reader,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        await sink.ExecuteAsync(context, CancellationToken.None);

        var geoJson = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("\"FeatureCollection\"", geoJson, StringComparison.Ordinal);
        Assert.Contains("\"geometry\"", geoJson, StringComparison.Ordinal);
        Assert.Contains("\"coordinates\"", geoJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinkGeoJsonOmitsNullAndEmptyPropertiesByDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.filter-default.geojson");

        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("name", FieldType.Utf8String, true),
            new FieldDef("geom", FieldType.Geometry, true)));

        var idColumn = new long[] { 1 };
        var nameColumn = RecordBatch.CreateUtf8Column(new string?[] { string.Empty });
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geomColumn = new GeometryColumn(new NetTopologySuite.Geometries.Geometry?[]
        {
            geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(5, 52)),
        });

        var batch = new RecordBatch(schema, 1, new object?[] { idColumn, nameColumn, geomColumn });

        var channel = Channel.CreateUnbounded<IRecordBatch>();
        await channel.Writer.WriteAsync(batch);
        channel.Writer.TryComplete();

        var sink = new SinkGeoJsonBlock(outputPath, includeProperties: true);
        var context = CreateSinkContext(channel.Reader);

        await sink.ExecuteAsync(context, CancellationToken.None);

        var geoJson = await File.ReadAllTextAsync(outputPath);
        using var doc = JsonDocument.Parse(geoJson);
        var properties = doc.RootElement
            .GetProperty("features")[0]
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("id", out _));
        Assert.False(properties.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task SinkGeoJsonIncludesNullAndEmptyPropertiesWhenEnabled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.filter-enabled.geojson");

        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("name", FieldType.Utf8String, true),
            new FieldDef("geom", FieldType.Geometry, true)));

        var idColumn = new long[] { 1 };
        var nameColumn = RecordBatch.CreateUtf8Column(new string?[] { string.Empty });
        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geomColumn = new GeometryColumn(new NetTopologySuite.Geometries.Geometry?[]
        {
            geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(5, 52)),
        });

        var batch = new RecordBatch(schema, 1, new object?[] { idColumn, nameColumn, geomColumn });

        var channel = Channel.CreateUnbounded<IRecordBatch>();
        await channel.Writer.WriteAsync(batch);
        channel.Writer.TryComplete();

        var sink = new SinkGeoJsonBlock(
            outputPath,
            includeProperties: true,
            includeNullAndEmptyProperties: true);
        var context = CreateSinkContext(channel.Reader);

        await sink.ExecuteAsync(context, CancellationToken.None);

        var geoJson = await File.ReadAllTextAsync(outputPath);
        using var doc = JsonDocument.Parse(geoJson);
        var properties = doc.RootElement
            .GetProperty("features")[0]
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("name", out var nameElement));
        Assert.True(nameElement.ValueKind is JsonValueKind.String or JsonValueKind.Null);
        if (nameElement.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(string.Empty, nameElement.GetString());
        }
    }

    [Fact]
    public void SourceCsvDeriveOutputSchemaInfersPrimitiveTypes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "typed.csv");
        File.WriteAllLines(inputPath,
        [
            "id,score,isActive,name",
            "1,12.5,true,alpha",
            "2,8.0,false,beta",
        ]);

        var block = new SourceCsvBlock(path: inputPath, hasHeader: true, inferTypes: true);
        var schema = block.DeriveOutputSchema();

        Assert.Equal(FieldType.Int64, schema.Fields[0].Type);
        Assert.Equal(FieldType.Float64, schema.Fields[1].Type);
        Assert.Equal(FieldType.Boolean, schema.Fields[2].Type);
        Assert.Equal(FieldType.Utf8String, schema.Fields[3].Type);
    }

    [Fact]
    public async Task SourceCsvResumesFromCheckpointOffset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "stride-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "input.csv");
        var checkpointPath = Path.Combine(tempDir, "checkpoint.txt");

        await File.WriteAllLinesAsync(inputPath,
        [
            "id,name",
            "1,a",
            "2,b",
            "3,c",
        ]);

        await File.WriteAllTextAsync(checkpointPath, "1");

        var block = new SourceCsvBlock(path: inputPath, hasHeader: true);
        var context = new BlockContext(
            "source",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            new BlockParams(new Dictionary<string, object?>
            {
                ["checkpointPath"] = checkpointPath,
            }),
            CancellationToken.None);

        var values = new List<string>();
        await foreach (var batch in block.ExecuteAsync(context, CancellationToken.None))
        {
            for (var row = 0; row < batch.RowCount; row++)
            {
                values.Add(batch.GetValueAsString(0, row));
            }
        }

        Assert.Equal(["2", "3"], values);
    }

    [Fact]
    public async Task SourceDataGeneratorEmitsConfiguredRowsWithExpectedSchema()
    {
        const int maxRows = 5;
        const int batchSize = 2;

        var block = new SourceDataGenerator(maxRows: maxRows, batchSize: batchSize, seed: 123);
        var schema = block.DeriveOutputSchema();

        Assert.Equal(3, schema.Fields.Length);
        Assert.Equal("id", schema.Fields[0].Name);
        Assert.Equal(FieldType.Int64, schema.Fields[0].Type);
        Assert.Equal("longitude", schema.Fields[1].Name);
        Assert.Equal(FieldType.Float64, schema.Fields[1].Type);
        Assert.Equal("latitude", schema.Fields[2].Name);
        Assert.Equal(FieldType.Float64, schema.Fields[2].Type);

        var context = new BlockContext(
            "source-generator",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

        var emittedRows = 0;
        var emittedBatches = 0;

        await foreach (var batch in block.ExecuteAsync(context, CancellationToken.None))
        {
            emittedBatches++;
            Assert.InRange(batch.RowCount, 1, batchSize);

            var ids = batch.Column<long>(0);
            var longitudes = batch.Column<double>(1);
            var latitudes = batch.Column<double>(2);

            for (var row = 0; row < batch.RowCount; row++)
            {
                Assert.Equal(emittedRows + row, ids[row]);
                Assert.InRange(longitudes[row], 6.4, 6.6);
                Assert.InRange(latitudes[row], 52.2, 52.4);
            }

            emittedRows += batch.RowCount;
        }

        Assert.Equal(maxRows, emittedRows);
        Assert.Equal(3, emittedBatches);
    }

    private sealed class TestSpillManager : ISpillManager
    {
        public ValueTask<ISpillScope> BeginScopeAsync(string blockId, CancellationToken cancellationToken)
            => ValueTask.FromResult<ISpillScope>(new TestSpillScope());
    }

    private sealed class TestSpillScope : ISpillScope
    {
        private readonly List<byte[]> _payloads = [];

        public ValueTask<string> WritePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _payloads.Add(payload.ToArray());
            return ValueTask.FromResult($"payload-{_payloads.Count}");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadPayloadsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var payload in _payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return payload;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestMetricsSink : IBlockMetricsSink
    {
        public void OnBatchIn(string nodeId, int rowCount, int bytes)
        {
        }

        public void OnBatchOut(string nodeId, int rowCount, int bytes)
        {
        }

        public void OnError(string nodeId)
        {
        }
    }

    private sealed class TestErrorSink : IErrorSink
    {
        public ValueTask WriteRecordErrorAsync(string nodeId, string message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static BlockContext CreateSinkContext(ChannelReader<IRecordBatch> input)
        => new(
            "sink",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            BlockParams.Empty,
            CancellationToken.None);

    private static BlockContext CreateTransformContext(ChannelReader<IRecordBatch> input, BlockParams? parameters = null)
        => new(
            "transform",
            new Dictionary<string, ChannelReader<IRecordBatch>>(StringComparer.OrdinalIgnoreCase)
            {
                ["in"] = input,
            },
            new Dictionary<string, ChannelWriter<IRecordBatch>>(StringComparer.OrdinalIgnoreCase),
            new TestSpillManager(),
            new TestMetricsSink(),
            new TestErrorSink(),
            ErrorPolicy.StopPipeline,
            parameters ?? BlockParams.Empty,
            CancellationToken.None);

    private static async Task<List<string[]>> ReadRowsAsync(ChannelReader<IRecordBatch> reader)
    {
        var rows = new List<string[]>();
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                for (var row = 0; row < batch.RowCount; row++)
                {
                    var values = new string[batch.Schema.Fields.Length];
                    for (var col = 0; col < batch.Schema.Fields.Length; col++)
                    {
                        values[col] = batch.GetValueAsString(col, row);
                    }

                    rows.Add(values);
                }
            }
        }

        return rows;
    }

    private static string ReadExcelCellText(Cell cell)
        => cell.InlineString?.Text?.Text
           ?? cell.CellValue?.Text
           ?? cell.InnerText;
}
