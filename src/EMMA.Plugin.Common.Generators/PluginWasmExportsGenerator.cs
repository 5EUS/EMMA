using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace EMMA.Plugin.Common.Generators;

/// <summary>
/// Generates WASM export scaffolding and dispatch glue for plugin entry points annotated with <c>PluginWasmExportsAttribute</c>.
/// </summary>
[Generator]
public sealed class PluginWasmExportsGenerator : IIncrementalGenerator
{
    private const string AttributeName = "EMMA.Plugin.Common.PluginWasmExportsAttribute";

    private static readonly ImmutableArray<OperationSpec> StandardOperations =
    [
        new("Handshake", "handshake", "PluginOperationNames.Handshake", 0, "PluginInvokeHelper.Invoke0"),
        new("Capabilities", "capabilities", "PluginOperationNames.Capabilities", 0, "PluginInvokeHelper.Invoke0"),
        new("Search", "search", "PluginOperationNames.Search", 2, "PluginInvokeHelper.Invoke2"),
        new("Chapters", "chapters", "PluginOperationNames.Chapters", 2, "PluginInvokeHelper.Invoke2"),
        new("Page", "page", "PluginOperationNames.Page", 4, "PluginInvokeHelper.Invoke4"),
        new("Pages", "pages", "PluginOperationNames.Pages", 5, "PluginInvokeHelper.Invoke5"),
        new("Invoke", "invoke", "PluginOperationNames.Invoke", 1, "PluginInvokeHelper.Invoke1"),
    ];

    /// <summary>
    /// Registers the incremental source generation pipeline for plugin WASM export stubs.
    /// </summary>
    /// <param name="context">The generator initialization context used to register syntax and output pipelines.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var annotatedPrograms = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
            static (ctx, _) => CreateModel(ctx))
            .Where(static model => model is not null);

        context.RegisterSourceOutput(annotatedPrograms, static (spc, model) => Emit(spc, model!));
    }

    private static ExportModel? CreateModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol programType)
        {
            return null;
        }

        var attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length < 2)
        {
            return null;
        }

        var hostType = attribute.ConstructorArguments[0].Value as INamedTypeSymbol;
        var jsonContextType = attribute.ConstructorArguments[1].Value as INamedTypeSymbol;
        if (hostType is null || jsonContextType is null)
        {
            return null;
        }

        var operations = new List<ResolvedOperation>();
        foreach (var operation in StandardOperations)
        {
            var method = hostType.GetMembers(operation.HostMethodName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => candidate.Parameters.Length == operation.ParameterCount);

            if (method is null)
            {
                return null;
            }

            operations.Add(new ResolvedOperation(operation, method));
        }

        var extraSerializableTypes = ImmutableArray<ITypeSymbol>.Empty;
        if (attribute.ConstructorArguments.Length > 2)
        {
            extraSerializableTypes = attribute.ConstructorArguments[2]
                .Values
                .Select(static arg => arg.Value as ITypeSymbol)
                .Where(static symbol => symbol is not null)
                .Cast<ITypeSymbol>()
                .ToImmutableArray();
        }

        return new ExportModel(programType, hostType, jsonContextType, operations.ToImmutableArray(), extraSerializableTypes);
    }

    private static void Emit(SourceProductionContext context, ExportModel model)
    {
        context.AddSource(
            $"{model.ProgramType.Name}.PluginWasmExports.g.cs",
            BuildProgramSource(model));
    }

    private static string BuildProgramSource(ExportModel model)
    {
        var ns = model.ProgramType.ContainingNamespace.IsGlobalNamespace
            ? null
            : model.ProgramType.ContainingNamespace.ToDisplayString();

        var builder = new StringBuilder();
        builder.AppendLine("#if PLUGIN_TRANSPORT_WASM");
        builder.AppendLine("using global::System;");
        builder.AppendLine("using global::System.Collections.Generic;");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(ns))
        {
            builder.Append("namespace ").Append(ns).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append("public static partial class ").Append(model.ProgramType.Name).AppendLine();
        builder.AppendLine("{");
        builder.Append("    private static readonly ")
            .Append(GetTypeName(model.HostType))
            .AppendLine(" OperationHost = new();");
        builder.AppendLine();
        builder.AppendLine("    private static readonly IReadOnlyDictionary<string, Delegate> WasmDispatch = new Dictionary<string, Delegate>(StringComparer.Ordinal)");
        builder.AppendLine("    {");
        foreach (var operation in model.Operations)
        {
            builder.Append("        [global::EMMA.Plugin.Common.")
                .Append(operation.Spec.OperationConstant)
                .Append("] = (")
                .Append(GetDelegateType(operation.Method))
                .Append(")(")
                .Append(GetDelegateLambda(operation.Method))
                .AppendLine("),");
        }
        builder.AppendLine("    };");
        builder.AppendLine();

        foreach (var operation in model.Operations)
        {
            builder.Append("    public static ")
                .Append(GetReturnTypeName(operation.Method.ReturnType))
                .Append(' ')
                .Append(operation.Spec.GeneratedMethodName)
                .Append('(')
                .Append(GetParameters(operation.Method))
                .Append(") => global::EMMA.Plugin.Common.")
                .Append(operation.Spec.InvokeHelperName)
                .Append('<')
                .Append(GetInvokeHelperTypeArguments(operation.Method))
                .Append(">(WasmDispatch, global::EMMA.Plugin.Common.")
                .Append(operation.Spec.OperationConstant);

            if (operation.Method.Parameters.Length > 0)
            {
                builder.Append(", ")
                    .Append(string.Join(", ", operation.Method.Parameters.Select(static parameter => parameter.Name)));
            }

            builder.AppendLine(");");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        builder.AppendLine("#endif");
        return builder.ToString();
    }

    private static string GetDelegateType(IMethodSymbol method)
    {
        var typeArguments = method.Parameters
            .Select(static parameter => GetTypeName(parameter.Type))
            .Concat([GetReturnTypeName(method.ReturnType)]);
        return $"global::System.Func<{string.Join(", ", typeArguments)}>";
    }

    private static string GetDelegateLambda(IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
        {
            return $"() => OperationHost.{method.Name}()";
        }

        var parameterList = string.Join(", ", method.Parameters.Select(static parameter => parameter.Name));
        return $"({parameterList}) => OperationHost.{method.Name}({parameterList})";
    }

    private static string GetParameters(IMethodSymbol method)
    {
        return string.Join(", ", method.Parameters.Select(static parameter => $"{GetTypeName(parameter.Type)} {parameter.Name}"));
    }

    private static string GetInvokeHelperTypeArguments(IMethodSymbol method)
    {
        return string.Join(", ", method.Parameters.Select(static parameter => GetTypeName(parameter.Type)).Concat(new[] { GetReturnTypeName(method.ReturnType) }));
    }

    private static string GetReturnTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType && named.Name == "Nullable" && named.TypeArguments.Length == 1)
        {
            return GetTypeName(named.TypeArguments[0]) + "?";
        }

        return GetTypeName(type);
    }

    private static string GetTypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private sealed class ExportModel
    {
        public ExportModel(
            INamedTypeSymbol programType,
            INamedTypeSymbol hostType,
            INamedTypeSymbol jsonContextType,
            ImmutableArray<ResolvedOperation> operations,
            ImmutableArray<ITypeSymbol> extraSerializableTypes)
        {
            ProgramType = programType;
            HostType = hostType;
            JsonContextType = jsonContextType;
            Operations = operations;
            ExtraSerializableTypes = extraSerializableTypes;
        }

        public INamedTypeSymbol ProgramType { get; }

        public INamedTypeSymbol HostType { get; }

        public INamedTypeSymbol JsonContextType { get; }

        public ImmutableArray<ResolvedOperation> Operations { get; }

        public ImmutableArray<ITypeSymbol> ExtraSerializableTypes { get; }
    }

    private sealed class OperationSpec
    {
        public OperationSpec(
            string hostMethodName,
            string generatedMethodName,
            string operationConstant,
            int parameterCount,
            string invokeHelperName)
        {
            HostMethodName = hostMethodName;
            GeneratedMethodName = generatedMethodName;
            OperationConstant = operationConstant;
            ParameterCount = parameterCount;
            InvokeHelperName = invokeHelperName;
        }

        public string HostMethodName { get; }

        public string GeneratedMethodName { get; }

        public string OperationConstant { get; }

        public int ParameterCount { get; }

        public string InvokeHelperName { get; }
    }

    private sealed class ResolvedOperation
    {
        public ResolvedOperation(OperationSpec spec, IMethodSymbol method)
        {
            Spec = spec;
            Method = method;
        }

        public OperationSpec Spec { get; }

        public IMethodSymbol Method { get; }
    }
}