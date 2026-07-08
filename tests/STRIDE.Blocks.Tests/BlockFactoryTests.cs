using STRIDE.Abstractions;
using STRIDE.Blocks;

namespace STRIDE.Blocks.Tests;

public class BlockFactoryTests
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
}
