using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace STRIDE.SourceGen;

[Generator]
public sealed class BlockRegistryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetBlockInfo(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        var collected = candidates.Collect();

        context.RegisterSourceOutput(collected, static (spc, blocks) =>
        {
            var source = GenerateFactorySource(blocks);
            spc.AddSource("GeneratedBlockFactory.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static BlockInfo? GetBlockInfo(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax classDeclaration)
        {
            return null;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol)
        {
            return null;
        }

        var strideAttribute = classSymbol.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "STRIDE.Abstractions.StrideBlockAttribute");
        if (strideAttribute is null || strideAttribute.ConstructorArguments.Length != 1)
        {
            return null;
        }

        var typeLiteral = strideAttribute.ConstructorArguments[0].Value?.ToString();
        if (string.IsNullOrWhiteSpace(typeLiteral))
        {
            return null;
        }

        var constructor = classSymbol.InstanceConstructors
            .Where(static ctor => ctor.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(static ctor => ctor.Parameters.Length)
            .FirstOrDefault();

        if (constructor is null)
        {
            return null;
        }

        var parameters = constructor.Parameters
            .Select(static parameter => new ParameterInfo(
                parameter.Name,
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameter.Type.SpecialType,
                parameter.HasExplicitDefaultValue,
                GetDefaultLiteral(parameter),
                parameter.NullableAnnotation == NullableAnnotation.Annotated))
            .ToImmutableArray();

        return new BlockInfo(
            typeLiteral!,
            classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            parameters);
    }

    private static string GetDefaultLiteral(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
        {
            return string.Empty;
        }

        var value = parameter.ExplicitDefaultValue;
        return value switch
        {
            null => "null",
            bool booleanValue => booleanValue ? "true" : "false",
            char charValue => $"'{charValue}'",
            string stringValue => $"\"{Escape(stringValue)}\"",
            _ => value.ToString() ?? "default",
        };
    }

    private static string GenerateFactorySource(ImmutableArray<BlockInfo> blocks)
    {
        var uniqueBlocks = blocks
            .GroupBy(static block => block.TypeLiteral, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static block => block.TypeLiteral, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using STRIDE.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace STRIDE.Blocks;");
        sb.AppendLine();
        sb.AppendLine("public sealed class GeneratedBlockFactory : IBlockFactory");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly HashSet<string> s_registeredTypes = new(StringComparer.Ordinal)");
        sb.AppendLine("    {");
        foreach (var block in uniqueBlocks)
        {
            sb.Append("        \"").Append(Escape(block.TypeLiteral)).AppendLine("\",");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public IReadOnlySet<string> RegisteredTypes => s_registeredTypes;");
        sb.AppendLine();
        sb.AppendLine("    public object Create(string type, BlockParams parameters)");
        sb.AppendLine("        => type switch");
        sb.AppendLine("        {");
        foreach (var block in uniqueBlocks)
        {
            sb.Append("            \"").Append(Escape(block.TypeLiteral)).Append("\" => BlockParamBinders.Bind")
                .Append(SanitizeIdentifier(block.TypeLiteral)).AppendLine("(parameters),");
        }

        sb.AppendLine("            _ => throw new UnknownBlockTypeException(type),");
        sb.AppendLine("        };");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("internal static class BlockParamBinders");
        sb.AppendLine("{");

        foreach (var block in uniqueBlocks)
        {
            sb.Append("    public static object Bind").Append(SanitizeIdentifier(block.TypeLiteral)).AppendLine("(BlockParams parameters)");
            sb.AppendLine("    {");
            sb.Append("        return new ").Append(block.FullyQualifiedClassName).AppendLine("(");
            for (var i = 0; i < block.ConstructorParameters.Length; i++)
            {
                var parameter = block.ConstructorParameters[i];
                var expression = BuildParameterExpression(parameter);
                sb.Append("            ").Append(parameter.Name).Append(": ").Append(expression);
                if (i < block.ConstructorParameters.Length - 1)
                {
                    sb.Append(',');
                }

                sb.AppendLine();
            }

            sb.AppendLine("        );");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    private static char GetRequiredChar(BlockParams parameters, string key)");
        sb.AppendLine("    {");
        sb.AppendLine("        var value = parameters.GetRequiredString(key);");
        sb.AppendLine("        if (value.Length == 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            throw new InvalidOperationException($\"Parameter '{key}' cannot be empty.\");");
        sb.AppendLine("        }");
        sb.AppendLine("        return value[0];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static char? GetOptionalChar(BlockParams parameters, string key)");
        sb.AppendLine("    {");
        sb.AppendLine("        var value = parameters.GetOptionalString(key);");
        sb.AppendLine("        if (string.IsNullOrEmpty(value))");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("        return value[0];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildParameterExpression(ParameterInfo parameter)
    {
        if (parameter.SpecialType == SpecialType.System_String)
        {
            if (parameter.HasDefaultValue)
            {
                return $"parameters.GetOptionalString(\"{parameter.Name}\") ?? {parameter.DefaultLiteral}";
            }

            return $"parameters.GetRequiredString(\"{parameter.Name}\")";
        }

        if (parameter.SpecialType == SpecialType.System_Int32)
        {
            if (parameter.HasDefaultValue)
            {
                return $"parameters.GetOptionalInt32(\"{parameter.Name}\") ?? {parameter.DefaultLiteral}";
            }

            return $"parameters.GetInt32(\"{parameter.Name}\")";
        }

        if (parameter.SpecialType == SpecialType.System_Double)
        {
            if (parameter.HasDefaultValue)
            {
                return $"parameters.GetOptionalDouble(\"{parameter.Name}\") ?? {parameter.DefaultLiteral}";
            }

            return $"parameters.GetDouble(\"{parameter.Name}\")";
        }

        if (parameter.SpecialType == SpecialType.System_Boolean)
        {
            if (parameter.HasDefaultValue)
            {
                return $"parameters.GetOptionalBoolean(\"{parameter.Name}\") ?? {parameter.DefaultLiteral}";
            }

            return $"parameters.GetBoolean(\"{parameter.Name}\")";
        }

        if (parameter.SpecialType == SpecialType.System_Char)
        {
            if (parameter.HasDefaultValue)
            {
                return $"(GetOptionalChar(parameters, \"{parameter.Name}\") ?? {parameter.DefaultLiteral})";
            }

            return $"GetRequiredChar(parameters, \"{parameter.Name}\")";
        }

        return parameter.HasDefaultValue ? parameter.DefaultLiteral : $"throw new InvalidOperationException(\"Unsupported constructor parameter type '{Escape(parameter.FullyQualifiedTypeName)}' for parameter '{parameter.Name}'.\")";
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class BlockInfo
    {
        public BlockInfo(string typeLiteral, string fullyQualifiedClassName, ImmutableArray<ParameterInfo> constructorParameters)
        {
            TypeLiteral = typeLiteral;
            FullyQualifiedClassName = fullyQualifiedClassName;
            ConstructorParameters = constructorParameters;
        }

        public string TypeLiteral { get; }

        public string FullyQualifiedClassName { get; }

        public ImmutableArray<ParameterInfo> ConstructorParameters { get; }
    }

    private sealed class ParameterInfo
    {
        public ParameterInfo(string name, string fullyQualifiedTypeName, SpecialType specialType, bool hasDefaultValue, string defaultLiteral, bool isNullable)
        {
            Name = name;
            FullyQualifiedTypeName = fullyQualifiedTypeName;
            SpecialType = specialType;
            HasDefaultValue = hasDefaultValue;
            DefaultLiteral = defaultLiteral;
            IsNullable = isNullable;
        }

        public string Name { get; }

        public string FullyQualifiedTypeName { get; }

        public SpecialType SpecialType { get; }

        public bool HasDefaultValue { get; }

        public string DefaultLiteral { get; }

        public bool IsNullable { get; }
    }
}
