using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace STRIDE.Schema;

public sealed class SecretResolver(IReadOnlyDictionary<string, string> environmentVariables, bool allowEmpty = false)
{
    private static readonly Regex SecretRegex = new(@"\${(?<var>[A-Z0-9_]+)}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void ResolveSecretsInGraph(YamlMappingNode rootNode)
    {
        VisitNode(rootNode);
    }

    private void VisitNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    VisitNode(pair.Key);
                    VisitNode(pair.Value);
                }
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    VisitNode(child);
                }
                break;

            case YamlScalarNode scalar when scalar.Value is not null:
                scalar.Value = ResolveText(scalar.Value);
                break;
        }
    }

    private string ResolveText(string input)
    {
        return SecretRegex.Replace(input, match =>
        {
            string varName = match.Groups["var"].Value;
            if (environmentVariables.TryGetValue(varName, out string? value))
            {
                return value;
            }

            if (allowEmpty) return string.Empty;

            throw new InvalidOperationException($"Workflow-configuratie fout: Omgevingsvariabele '{varName}' is niet gedefinieerd.");
        });
    }
}