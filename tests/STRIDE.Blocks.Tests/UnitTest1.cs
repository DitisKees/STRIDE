using STRIDE.Abstractions;
using STRIDE.Blocks;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using NetTopologySuite.Geometries;

namespace STRIDE.Blocks.Tests;

public class UnitTest1
{
    [Fact]
    public void GeneratedFactoryExposesKnownTypes()
    {
        var factory = new GeneratedBlockFactory();

        Assert.Contains("SourceCsv", factory.RegisteredTypes);
        Assert.Contains("SourcePostGresQL", factory.RegisteredTypes);
        Assert.Contains("SourcePostGis", factory.RegisteredTypes);
        Assert.Contains("SourceGeoJson", factory.RegisteredTypes);
        Assert.Contains("SourceJson", factory.RegisteredTypes);
        Assert.Contains("SourceGml", factory.RegisteredTypes);
        Assert.Contains("SourceShapefile", factory.RegisteredTypes);
        Assert.Contains("SourceGeoPackage", factory.RegisteredTypes);
        Assert.Contains("SourceExcel", factory.RegisteredTypes);
        Assert.Contains("SourceWfs", factory.RegisteredTypes);
        Assert.Contains("TransformAggregator", factory.RegisteredTypes);
        Assert.Contains("TransformBuffer", factory.RegisteredTypes);
        Assert.Contains("TransformReproject", factory.RegisteredTypes);
        Assert.Contains("TransformSpatialJoin", factory.RegisteredTypes);
        Assert.Contains("TransformConditionalSplitter", factory.RegisteredTypes);
        Assert.Contains("TransformFilter", factory.RegisteredTypes);
        Assert.Contains("SinkCsv", factory.RegisteredTypes);
        Assert.Contains("SinkGeoJson", factory.RegisteredTypes);
        Assert.Contains("SinkJson", factory.RegisteredTypes);
        Assert.Contains("SinkPostGis", factory.RegisteredTypes);
        Assert.Contains("SinkExcel", factory.RegisteredTypes);
        Assert.Contains("SinkWfsT", factory.RegisteredTypes);
    }

    [Fact]
    public void GeneratedFactoryCreatesConfiguredBlocks()
    {
        var factory = new GeneratedBlockFactory();

        var source = factory.Create(
            "SourceCsv",
            new BlockParams(new Dictionary<string, object?>
            {
                ["path"] = "./input.csv",
                ["columns"] = "id,status",
            }));
        var filter = factory.Create(
            "TransformFilter",
            new BlockParams(new Dictionary<string, object?>
            {
                ["when"] = "id > 10",
            }));
        var aggregator = factory.Create(
            "TransformAggregator",
            new BlockParams(new Dictionary<string, object?>
            {
                ["groupBy"] = "status",
                ["aggregates"] = "count:*:row_count",
            }));
        var sink = factory.Create(
            "SinkCsv",
            new BlockParams(new Dictionary<string, object?>
            {
                ["path"] = "./output.csv",
            }));

        Assert.IsType<SourceCsvBlock>(source);
        Assert.IsType<TransformFilterBlock>(filter);
        Assert.IsType<TransformAggregatorBlock>(aggregator);
        Assert.IsType<SinkCsvBlock>(sink);
    }

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

    private static BlockContext CreateTransformContext(ChannelReader<IRecordBatch> input)
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
            BlockParams.Empty,
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
}
