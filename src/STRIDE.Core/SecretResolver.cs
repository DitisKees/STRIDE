using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace STRIDE.Core;

public sealed partial class SecretResolver
{
    public string ResolveScalars(
        string yaml,
        Func<string, string?> variableLookup,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(variableLookup);

        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        foreach (var document in stream.Documents)
        {
            ResolveNode(document.RootNode, variableLookup, allowEmpty);
        }

        using var writer = new StringWriter();
        stream.Save(writer, false);
        return writer.ToString();
    }

    private static void ResolveNode(
        YamlNode node,
        Func<string, string?> variableLookup,
        bool allowEmpty)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                ResolveScalar(scalar, variableLookup, allowEmpty);
                break;

            case YamlMappingNode mapping:
                foreach (var entry in mapping.Children)
                {
                    ResolveNode(entry.Key, variableLookup, allowEmpty);
                    ResolveNode(entry.Value, variableLookup, allowEmpty);
                }
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    ResolveNode(child, variableLookup, allowEmpty);
                }
                break;
        }
    }

    private static void ResolveScalar(
        YamlScalarNode scalar,
        Func<string, string?> variableLookup,
        bool allowEmpty)
    {
        if (string.IsNullOrEmpty(scalar.Value))
        {
            return;
        }

        var replaced = VariablePattern().Replace(scalar.Value, match =>
        {
            var variableName = match.Groups[1].Value;
            var value = variableLookup(variableName);
            if (value is null)
            {
                if (allowEmpty)
                {
                    return string.Empty;
                }

                throw new InvalidOperationException($"Missing required variable '{variableName}'.");
            }

            return value;
        });

        scalar.Value = replaced;
        scalar.Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted;
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VariablePattern();
}