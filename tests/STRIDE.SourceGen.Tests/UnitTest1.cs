namespace STRIDE.SourceGen.Tests;

public class UnitTest1
{
    [Fact]
    public void BlockRegistryGeneratorImplementsIncrementalGenerator()
    {
        var generatorType = typeof(STRIDE.SourceGen.BlockRegistryGenerator);
        Assert.Contains(generatorType.GetInterfaces(), static i => string.Equals(i.Name, "IIncrementalGenerator", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockRegistryGeneratorCanBeInstantiated()
    {
        var generator = new STRIDE.SourceGen.BlockRegistryGenerator();
        Assert.NotNull(generator);
    }
}
