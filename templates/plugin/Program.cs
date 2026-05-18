#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.AspNetCore;
using EMMA.Plugin.Common;
using EMMA.TemplatePlugin.ASPNET;
using Microsoft.Extensions.DependencyInjection;
#else
using EMMA.Plugin.Common;
using EMMA.TemplatePlugin.WASM;
#endif

namespace EMMA.TemplatePlugin;

#if PLUGIN_TRANSPORT_WASM
[PluginWasmExports(
    typeof(WasmPluginOperationHost),
    typeof(WasmJsonContext),
    typeof(WasmChapterOperationItem[]),
    typeof(SearchSuggestionItem[]),
    typeof(BenchmarkResult),
    typeof(NetworkBenchmarkResult),
    ExportBridgeNamespace = "LibraryWorld.wit.exports.emma.plugin")]
#endif
public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    private static readonly PluginSdkManifestDefaultsOptions HostDefaults = new(
        PluginManifestFileName: "EMMA.TemplatePlugin.plugin.json",
        Fallback: new PluginManifestDefaults(
            250,
            512,
            ["example.invalid"],
            []),
        PluginProjectFolderName: "EMMA.TemplatePlugin",
        DefaultPort: 5000,
        DevelopmentPortEnvironmentVariables: ["EMMA_PLUGIN_PORT"],
        ProductionPortEnvironmentVariables: ["EMMA_PLUGIN_PORT"],
        DevelopmentPortArgumentName: "--port",
        ProductionPortArgumentName: string.Empty,
        RootMessage: "EMMA template plugin is running.");

    public static void Main(string[] args)
    {
        var devMode = PluginEnvironment.IsDevelopmentMode();
        PluginBuilder.CreateWithDefaults(args, HostDefaults)
            .ConfigureServices(services =>
            {
                services.AddTransient<IPluginSearchMetadataRuntime>(static provider => provider.GetRequiredService<AspNetClient>());
                services.AddTransient<IPluginSearchSuggestionsRuntime>(static provider => provider.GetRequiredService<AspNetClient>());
            })
            .ConfigureDefaultControl(ConfigureDefaultControlService)
                .AddDefaultPagedProviders<AspNetClient>()
            .Run(mapDefaultEndpoints: devMode);
    }

    private static void ConfigureDefaultControlService(PluginSdkControlOptions options)
    {
        options.Message = "EMMA template plugin ready";
        options.Capabilities.Add("template-plugin");
        options.Capabilities.Add("search");
        options.Capabilities.Add("pages");
    }
#else
    public static void Main(string[] args)
    {
        Environment.ExitCode = PluginWasmCliHost.Run(
            args,
            PluginOperationNames.WasmCliKnownOperations,
            OperationHost.ExecuteOperationForCli);
    }

    private static readonly WasmPluginOperationHost OperationHost = new();
#endif
}