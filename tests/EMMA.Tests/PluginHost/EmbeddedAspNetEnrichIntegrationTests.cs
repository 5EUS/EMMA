using System.Text.Json;
using EMMA.PluginHost.Library;

namespace EMMA.Tests.PluginHost;

public sealed class EmbeddedAspNetEnrichIntegrationTests
{
    [Fact]
    public void EnrichMediaJsonManaged_LocalAspNetPlugin_ReturnsRatingMetadata()
    {
        using var signedPluginsScope = new EnvironmentVariableScope("EMMA_REQUIRE_SIGNED_PLUGINS", "false");
        using var signedPluginsCompatScope = new EnvironmentVariableScope("PluginSignature__RequireSignedPlugins", "false");

        var emmaUiRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "com.example.emmaui",
            "emmaui");
        var manifestsDir = Path.Combine(emmaUiRoot, "manifests");
        var pluginsDir = Path.Combine(emmaUiRoot, "plugins");
        var pluginExecutable = Path.Combine(pluginsDir, "emma.plugin.test", "EMMA.TestPlugin");

        Assert.True(Directory.Exists(manifestsDir), $"Expected manifests directory at '{manifestsDir}'.");
        Assert.True(Directory.Exists(pluginsDir), $"Expected plugins directory at '{pluginsDir}'.");
        Assert.True(File.Exists(pluginExecutable), $"Expected native ASP.NET plugin entrypoint at '{pluginExecutable}'.");

        PluginHostExports.ShutdownManaged();

        try
        {
            var initResult = PluginHostExports.InitializeManaged(manifestsDir, pluginsDir);
            Assert.True(initResult == 0,
                $"InitializeManaged failed: {PluginHostExports.GetLastErrorManaged() ?? "<no error>"}");

            var openResult = PluginHostExports.OpenPluginManaged("emma.plugin.test");
            Assert.True(openResult == 1,
                $"OpenPluginManaged failed: {PluginHostExports.GetLastErrorManaged() ?? "<no error>"}\nDiscovered plugins: {PluginHostExports.ListPluginsJsonManaged() ?? "<null>"}");

            var searchJson = PluginHostExports.SearchJsonManaged("emma.plugin.test", "naruto", correlationId: "embedded-aspnet-test-search");
            Assert.False(string.IsNullOrWhiteSpace(searchJson));

            using var searchDocument = JsonDocument.Parse(searchJson!);
            var firstResult = searchDocument.RootElement.EnumerateArray().FirstOrDefault();
            Assert.Equal(JsonValueKind.Object, firstResult.ValueKind);
            var mediaPayload = BuildRuntimeEnrichPayload(firstResult);

            var enrichJson = PluginHostExports.EnrichMediaJsonManaged(
                "emma.plugin.test",
                mediaPayload,
                correlationId: "embedded-aspnet-test-enrich");

            Assert.False(string.IsNullOrWhiteSpace(enrichJson),
                $"EnrichMediaJsonManaged failed: {PluginHostExports.GetLastErrorManaged() ?? "<no error>"}. Search payload: {firstResult.GetRawText()}. Enrich payload: {mediaPayload}");

            using var enrichDocument = JsonDocument.Parse(enrichJson!);
            Assert.True(TryReadMetadataValue(enrichDocument.RootElement, "Rating", out var rating),
                $"Expected enrich result to include Rating metadata. Payload: {enrichJson}");
            if (string.IsNullOrWhiteSpace(rating))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Expected Rating metadata to have a value. Rating='{rating ?? "<null>"}', Payload: {enrichJson}");
            }
        }
        finally
        {
            PluginHostExports.ShutdownManaged();
        }
    }

    private static bool TryReadMetadataValue(JsonElement root, string key, out string? value)
    {
        value = null;
        if (!TryGetProperty(root, "metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string BuildRuntimeEnrichPayload(JsonElement searchResult)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = ReadRequiredString(searchResult, "Id"),
            ["sourceId"] = ReadString(searchResult, "SourceId") ?? string.Empty,
            ["source"] = ReadString(searchResult, "SourceId") ?? string.Empty,
            ["title"] = ReadString(searchResult, "Title") ?? string.Empty,
            ["mediaType"] = ReadString(searchResult, "MediaType") ?? string.Empty
        };

        var thumbnailUrl = ReadString(searchResult, "ThumbnailUrl");
        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            payload["thumbnailUrl"] = thumbnailUrl;
        }

        var description = ReadString(searchResult, "Description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            payload["description"] = description;
        }

        if (TryGetProperty(searchResult, "Metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            var normalizedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in metadata.EnumerateObject())
            {
                var normalizedValue = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(normalizedValue))
                {
                    normalizedMetadata[property.Name] = normalizedValue;
                }
            }

            if (normalizedMetadata.Count > 0)
            {
                payload["metadata"] = normalizedMetadata;
            }
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        var value = ReadString(element, name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Expected search result property '{name}' to be populated. Payload: {element.GetRawText()}");
        return value!;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object
            && (TryGetProperty(value, "Value", out var nested)
                || TryGetProperty(value, "value", out nested))
            && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        return value.ToString();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}