using YamlDotNet.RepresentationModel;

namespace STRIDE.Core.Tests;

public class SecretResolverTests
{
    [Fact]
    public void ResolveScalarsReplacesTemplateVariablesInScalarNodes()
    {
        var yaml = """
        version: "1.0"
        settings:
          errorLog: "${LOG_PATH}"
        """;

        var resolver = new SecretResolver();
        var resolved = resolver.ResolveScalars(yaml, key => key == "LOG_PATH" ? "./logs/errors.ndjson" : null);

        Assert.Contains("./logs/errors.ndjson", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveScalarsThrowsWhenVariableIsMissingAndAllowEmptyFalse()
    {
        var yaml = "value: ${MISSING}";
        var resolver = new SecretResolver();

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveScalars(yaml, _ => null));
    }

    [Fact]
    public void ResolveScalarsPreservesSpecialCharactersWithoutYamlInjection()
    {
        var yaml = "value: ${SECRET}";
        var secret = "line1\nline2: \"quoted\" #comment";

        var resolver = new SecretResolver();
        var resolved = resolver.ResolveScalars(yaml, key => key == "SECRET" ? secret : null);

        using var reader = new StringReader(resolved);
        var stream = new YamlStream();
        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var scalar = (YamlScalarNode)root.Children[new YamlScalarNode("value")];
        Assert.Equal(secret, scalar.Value);
        Assert.Single(root.Children);
    }
}
