using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.ObjectFactories;

namespace STRIDE.Schema;

public sealed class WorkflowLoader(IReadOnlyDictionary<string, string> envVars)
{
    private readonly SecretResolver _secretResolver = new(envVars);

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithObjectFactory(new RecordObjectFactory())
        .Build();

    public WorkflowConfig Load(string yamlContent)
    {
        using var reader = new StringReader(yamlContent);
        var yamlStream = new YamlStream();
        yamlStream.Load(reader);

        if (yamlStream.Documents.Count == 0 || yamlStream.Documents[0].RootNode is not YamlMappingNode rootNode)
        {
            throw new InvalidDataException("Ongeldig workflow-bestand: Geen geldige YAML root mapping gevonden.");
        }

        // 1. Vervang alle `${VAR}` patronen veilig binnen de abstract syntax tree van de YAML
        _secretResolver.ResolveSecretsInGraph(rootNode);

        // 2. Converteer de gemanipuleerde AST naar een string voor de deserializer
        var serializer = new SerializerBuilder().Build();
        using var writer = new StringWriter();
        serializer.Serialize(writer, rootNode);

        // 3. Deserialiseer naar het sterke object-model
        return _deserializer.Deserialize<WorkflowConfig>(writer.ToString());
    }

    // Door over te erven van DefaultObjectFactory hoeven we de rest van de interface niet handmatig te implementeren
    private sealed class RecordObjectFactory : DefaultObjectFactory
    {
        public override object Create(Type type)
        {
            if (type == typeof(WorkflowConfig))
                return new WorkflowConfig(default!, default!, default!, default!, default!);

            if (type == typeof(WorkflowNode))
                return new WorkflowNode(default!, default!, default!, default!, default!);

            if (type == typeof(WorkflowSink))
                return new WorkflowSink(default!, default!, default!, default!, default!);

            // Laat de basis factory het afhandelen voor Lists, Dictionaries en Primitives
            return base.Create(type);
        }
    }
}