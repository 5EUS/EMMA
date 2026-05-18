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
            var method = FindOperationMethod(hostType, operation.HostMethodName, operation.ParameterCount);

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

        string? exportBridgeNamespace = null;
        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "ExportBridgeNamespace"
                && namedArgument.Value.Value is string namespaceValue
                && !string.IsNullOrWhiteSpace(namespaceValue))
            {
                exportBridgeNamespace = namespaceValue;
                break;
            }
        }

        return new ExportModel(programType, hostType, jsonContextType, operations.ToImmutableArray(), extraSerializableTypes, exportBridgeNamespace);
    }

    private static IMethodSymbol? FindOperationMethod(INamedTypeSymbol hostType, string methodName, int parameterCount)
    {
        for (var current = hostType; current is not null; current = current.BaseType)
        {
            var method = current.GetMembers(methodName)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(candidate =>
                    !candidate.IsStatic
                    && candidate.MethodKind == MethodKind.Ordinary
                    && candidate.Parameters.Length == parameterCount);

            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    private static void Emit(SourceProductionContext context, ExportModel model)
    {
        context.AddSource(
            $"{model.ProgramType.Name}.PluginWasmExports.g.cs",
            BuildProgramSource(model));

        if (!string.IsNullOrWhiteSpace(model.ExportBridgeNamespace))
        {
            context.AddSource(
                $"{model.ProgramType.Name}.PluginImpl.g.cs",
                BuildTypedExportBridgeSource(model));
        }
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

    private static string BuildTypedExportBridgeSource(ExportModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#if PLUGIN_TRANSPORT_WASM");
        builder.AppendLine("using global::EMMA.Plugin.Common;");
        builder.AppendLine("using global::LibraryWorld;");
        builder.AppendLine("using global::LibraryWorld.wit.imports.emma.plugin;");
        builder.AppendLine();
        builder.Append("namespace ").Append(model.ExportBridgeNamespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("public static partial class PluginImpl");
        builder.AppendLine("{");
        builder.AppendLine("    public static IPlugin.HandshakeResponse Handshake()");
        builder.AppendLine("    {");
        builder.Append("        var handshake = ")
            .Append(GetTypeName(model.ProgramType))
            .AppendLine(".handshake();");
        builder.AppendLine("        return new IPlugin.HandshakeResponse(handshake.version, handshake.message);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static List<IPlugin.Capability> Capabilities()");
        builder.AppendLine("    {");
        builder.Append("        var capabilities = ")
            .Append(GetTypeName(model.ProgramType))
            .AppendLine(".capabilities();");
        builder.AppendLine("        return PluginTypedExportScaffold.MapList(");
        builder.AppendLine("            capabilities,");
        builder.AppendLine("            capability => new IPlugin.Capability(");
        builder.AppendLine("                capability.name,");
        builder.AppendLine("                [.. capability.mediaTypes],");
        builder.AppendLine("                [.. capability.operations]));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static List<IPlugin.MediaSearchItem> Search(string query, string payloadJson)");
        builder.AppendLine("    {");
        builder.AppendLine("        return PluginTypedExportScaffold.ResolveSearchResults(");
        builder.AppendLine("            query,");
        builder.AppendLine("            payloadJson,");
        builder.AppendLine("            ResolveSearchPayload,");
        builder.Append("            ").Append(GetTypeName(model.ProgramType)).AppendLine(".search,");
        builder.AppendLine("            static item => new IPlugin.MediaSearchItem(");
        builder.AppendLine("                item.id,");
        builder.AppendLine("                item.source,");
        builder.AppendLine("                item.title,");
        builder.AppendLine("                item.mediaType,");
        builder.AppendLine("                item.thumbnailUrl,");
        builder.AppendLine("                item.description,");
        builder.AppendLine("                PluginTypedExportScaffold.MapOptionalList(");
        builder.AppendLine("                    item.metadata,");
        builder.AppendLine("                    static metadataItem => new IPlugin.KeyValue(metadataItem.key, metadataItem.value))));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static List<IPlugin.ChapterItem> Chapters(string mediaId, string payloadJson)");
        builder.AppendLine("    {");
        builder.AppendLine("        return PluginTypedExportScaffold.ResolveChapterResults(");
        builder.AppendLine("            mediaId,");
        builder.AppendLine("            payloadJson,");
        builder.AppendLine("            ResolveChaptersPayload,");
        builder.Append("            ").Append(GetTypeName(model.ProgramType)).AppendLine(".chapters,");
        builder.AppendLine("            static item => new IPlugin.ChapterItem(");
        builder.AppendLine("                item.id,");
        builder.AppendLine("                checked((uint)item.number),");
        builder.AppendLine("                item.title,");
        builder.AppendLine("                [.. item.uploaderGroups ?? []]));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static IPlugin.PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)");
        builder.AppendLine("    {");
        builder.AppendLine("        return PluginTypedExportScaffold.ResolvePageResult(");
        builder.AppendLine("            mediaId,");
        builder.AppendLine("            chapterId,");
        builder.AppendLine("            pageIndex,");
        builder.AppendLine("            payloadJson,");
        builder.AppendLine("            ResolvePagePayload,");
        builder.Append("            ").Append(GetTypeName(model.ProgramType)).AppendLine(".page,");
        builder.AppendLine("            static value => new IPlugin.PageItem(value.id, checked((uint)value.index), value.contentUri));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static List<IPlugin.PageItem> Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)");
        builder.AppendLine("    {");
        builder.AppendLine("        return PluginTypedExportScaffold.ResolvePageResults(");
        builder.AppendLine("            mediaId,");
        builder.AppendLine("            chapterId,");
        builder.AppendLine("            startIndex,");
        builder.AppendLine("            count,");
        builder.AppendLine("            payloadJson,");
        builder.AppendLine("            ResolvePagesPayload,");
        builder.Append("            ").Append(GetTypeName(model.ProgramType)).AppendLine(".pages,");
        builder.AppendLine("            static page => new IPlugin.PageItem(page.id, checked((uint)page.index), page.contentUri));");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static IPlugin.MediaOperationResponse Invoke(IPlugin.MediaOperationRequest request)");
        builder.AppendLine("    {");
        builder.AppendLine("        return PluginTypedExportScaffold.InvokeWithOperationErrorHandling(");
        builder.AppendLine("            () =>");
        builder.AppendLine("            {");
        builder.AppendLine("                var payload = ResolveInvokePayload(request);");
        builder.Append("                var result = ").Append(GetTypeName(model.ProgramType)).AppendLine(".invoke(new OperationRequest(");
        builder.AppendLine("                    request.operation,");
        builder.AppendLine("                    request.mediaId,");
        builder.AppendLine("                    request.mediaType,");
        builder.AppendLine("                    request.argsJson,");
        builder.AppendLine("                    payload));");
        builder.AppendLine();
        builder.AppendLine("                return PluginTypedExportScaffold.ToOperationResponseOrThrow(");
        builder.AppendLine("                    result,");
        builder.AppendLine("                    static (contentType, payloadJson) => new IPlugin.MediaOperationResponse(contentType, payloadJson),");
        builder.AppendLine("                    static message => new WitException<IPlugin.OperationError>(IPlugin.OperationError.UnsupportedOperation(message), 0),");
        builder.AppendLine("                    static message => new WitException<IPlugin.OperationError>(IPlugin.OperationError.InvalidArguments(message), 0),");
        builder.AppendLine("                    static message => new WitException<IPlugin.OperationError>(IPlugin.OperationError.Failed(message), 0));");
        builder.AppendLine("            },");
        builder.AppendLine("            static message => new WitException<IPlugin.OperationError>(IPlugin.OperationError.Failed(message), 0));");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine("#endif");
        return builder.ToString();
    }

    private sealed class ExportModel
    {
        public ExportModel(
            INamedTypeSymbol programType,
            INamedTypeSymbol hostType,
            INamedTypeSymbol jsonContextType,
            ImmutableArray<ResolvedOperation> operations,
            ImmutableArray<ITypeSymbol> extraSerializableTypes,
            string? exportBridgeNamespace)
        {
            ProgramType = programType;
            HostType = hostType;
            JsonContextType = jsonContextType;
            Operations = operations;
            ExtraSerializableTypes = extraSerializableTypes;
            ExportBridgeNamespace = exportBridgeNamespace;
        }

        public INamedTypeSymbol ProgramType { get; }

        public INamedTypeSymbol HostType { get; }

        public INamedTypeSymbol JsonContextType { get; }

        public ImmutableArray<ResolvedOperation> Operations { get; }

        public ImmutableArray<ITypeSymbol> ExtraSerializableTypes { get; }

        public string? ExportBridgeNamespace { get; }
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