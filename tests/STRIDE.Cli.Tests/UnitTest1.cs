namespace STRIDE.Cli.Tests;

public class UnitTest1
{
    [Fact]
    public void CliAssemblyReferencesCoreBlocksAndSchema()
    {
        var assembly = System.Reflection.Assembly.Load("STRIDE.Cli");
        var references = assembly.GetReferencedAssemblies().Select(static a => a.Name).ToArray();

        Assert.Contains("STRIDE.Core", references);
        Assert.Contains("STRIDE.Blocks", references);
        Assert.Contains("STRIDE.Schema", references);
    }

    [Fact]
    public void CliAssemblyNameIsStable()
    {
        var assembly = System.Reflection.Assembly.Load("STRIDE.Cli");
        Assert.Equal("STRIDE.Cli", assembly.GetName().Name);
    }
}
