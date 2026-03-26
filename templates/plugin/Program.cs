#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.AspNetCore;
using EMMA.PluginTemplate.Infrastructure;
using EMMA.PluginTemplate.Services;
using Microsoft.Extensions.DependencyInjection;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace EMMA.PluginTemplate;

public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    public static void Main(string[] args)
    {
        var devMode = PluginEnvironment.IsDevelopmentMode();
        var hostOptions = new PluginAspNetHostOptions(
            DefaultPort: 5005,
            PortEnvironmentVariables: devMode
                ? ["EMMA_PLUGIN_PORT", "EMMA_TEST_PLUGIN_PORT"]
                : ["EMMA_PLUGIN_PORT"],
            PortArgumentName: devMode ? "--port" : string.Empty,
            RootMessage: "EMMA plugin template is running.");

        PluginBuilder.Create(args, hostOptions)
            .ConfigureServices(services =>
            {
                services.AddHttpClient<HttpJsonClient>(client =>
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-PluginTemplate/1.0");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                });
            })
            .UseDefaultControlService(options =>
            {
                options.Message = "EMMA plugin template ready";
                options.Capabilities.Add("search");
                options.Capabilities.Add("pages");
                options.Capabilities.Add("video");
            })
            .AddSearchProvider<SearchProviderService>()
            .AddPageProvider<PageProviderService>()
            .AddVideoProvider<VideoProviderService>()
            .Run(mapDefaultEndpoints: devMode);
    }
#else
    public static void Main(string[] args)
    {
        var operation = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        var json = ExecuteOperation(operation, args);

        if (string.IsNullOrWhiteSpace(json))
        {
            Environment.ExitCode = 2;
            Console.Error.WriteLine("Unsupported or invalid operation.");
            return;
        }

        Console.WriteLine(json);
    }

    private static string ExecuteOperation(string operation, string[] args)
    {
        try
        {
            return operation switch
            {
                "handshake" => JsonSerializer.Serialize(
                    new HandshakeResponse("1.0.0", "EMMA template wasm component ready"),
                    PluginTemplateWasmJsonContext.Default.HandshakeResponse),
                "capabilities" => JsonSerializer.Serialize(
                    ["health", "search", "paged", "pages"],
                    PluginTemplateWasmJsonContext.Default.StringArray),
                "search" => JsonSerializer.Serialize([], PluginTemplateWasmJsonContext.Default.SearchItemArray),
                "chapters" => JsonSerializer.Serialize([], PluginTemplateWasmJsonContext.Default.ChapterItemArray),
                "page" => "null",
                "pages" => JsonSerializer.Serialize([], PluginTemplateWasmJsonContext.Default.PageItemArray),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WASM operation '{operation}' failed: {ex}");
            return string.Empty;
        }
    }

    private sealed record HandshakeResponse(string version, string message);
    private sealed record SearchItem(string id, string source, string title, string mediaType, string? thumbnailUrl = null, string? description = null);
    private sealed record ChapterItem(string id, int number, string title);
    private sealed record PageItem(string id, int index, string contentUri);

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(PageItem[]))]
    private sealed partial class PluginTemplateWasmJsonContext : JsonSerializerContext
    {
    }
#endif
}
