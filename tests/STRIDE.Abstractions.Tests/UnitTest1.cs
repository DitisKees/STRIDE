using System.Collections.Immutable;
using STRIDE.Abstractions;
using NetTopologySuite.Geometries;

namespace STRIDE.Abstractions.Tests;

public class UnitTest1
{
    [Fact]
    public void SchemaBuildsOrdinalLookupAndGeometryIndex()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true),
            new FieldDef("name", FieldType.Utf8String, true)));

        Assert.Equal(1, schema.GeometryFieldIndex);
        Assert.True(schema.TryGetOrdinal("NAME", out var ordinal));
        Assert.Equal(2, ordinal);
    }

    [Fact]
    public void SelectRowsPreservesGeometryColumns()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("geom", FieldType.Geometry, true)));

        var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var idColumn = new long[] { 1, 2 };
        var geomColumn = new GeometryColumn(new Geometry?[]
        {
            geometryFactory.CreatePoint(new Coordinate(4, 52)),
            geometryFactory.CreatePoint(new Coordinate(5, 53)),
        });

        var batch = new RecordBatch(schema, 2, new object?[] { idColumn, geomColumn });
        var selected = batch.SelectRows(new int[] { 1 });

        Assert.Equal(1, selected.RowCount);
        Assert.Equal(2, selected.Column<long>(0)[0]);
        Assert.NotNull(selected.GeometryColumn(1).Values[0]);
        Assert.Equal("POINT (5 53)", selected.GeometryColumn(1).Values[0]!.AsText());
    }

    [Fact]
    public void ExpressionEvaluatorSupportsLogicalAndStringOperators()
    {
        var schema = new Schema(ImmutableArray.Create(
            new FieldDef("id", FieldType.Int64, false),
            new FieldDef("isActive", FieldType.Boolean, false),
            new FieldDef("status", FieldType.Utf8String, true)));

        var batch = RecordBatch.FromRows(schema,
        [
            ["1", "true", "active"],
            ["2", "false", "inactive"],
            ["3", "true", "archived"],
        ]);

        var evaluator = ExpressionEvaluator.Compile("(isActive == true && status startsWith \"act\") || id == 2");

        Assert.True(evaluator.Evaluate(batch, 0));
        Assert.True(evaluator.Evaluate(batch, 1));
        Assert.False(evaluator.Evaluate(batch, 2));
    }

    [Fact]
    public void ExpressionEvaluatorThrowsForUnknownField()
    {
        var schema = new Schema(ImmutableArray.Create(new FieldDef("id", FieldType.Int64, false)));
        var batch = RecordBatch.FromRows(schema, [["1"]]);
        var evaluator = ExpressionEvaluator.Compile("missing == 1");

        Assert.Throws<InvalidOperationException>(() => evaluator.Evaluate(batch, 0));
    }
}
