using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using EMMA.PluginHost.Library;

namespace EMMA.Tests.PluginHost;

[Collection("Embedded AspNet Plugin Integration")]
public sealed class EmbeddedAspNetEnrichIntegrationTests
{
    [Fact]
    public void EnrichMediaJsonManaged_LocalAspNetPlugin_ReturnsRatingMetadata()
    {
        using var signedPluginsScope = new EnvironmentVariableScope("EMMA_REQUIRE_SIGNED_PLUGINS", "false");
        using var signedPluginsCompatScope = new EnvironmentVariableScope("PluginSignature__RequireSignedPlugins", "false");
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-embedded-aspnet-enrich", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var manifestsDir = Path.Combine(tempRoot, "manifests");
        var pluginsDir = Path.Combine(tempRoot, "plugins");
        Directory.CreateDirectory(manifestsDir);
        Directory.CreateDirectory(pluginsDir);

        var entrypointPath = ResolveNativeTestPluginEntrypoint();
        CopyEntrypointToSandbox(entrypointPath, pluginsDir, "emma.plugin.test");

        var port = GetFreePort();
        var manifestPath = Path.Combine(manifestsDir, "emma.plugin.test.json");
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "id": "emma.plugin.test",
              "name": "EMMA Test Plugin",
              "version": "1.0.0",
              "protocol": "grpc",
              "endpoint": "http://127.0.0.1:{{port}}"
            }
            """);

        using var pluginPortScope = new EnvironmentVariableScope("EMMA_TEST_PLUGIN_PORT", port.ToString());

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
            TryDelete(tempRoot);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveNativeTestPluginEntrypoint()
    {
        var solutionRoot = ResolveSolutionRoot();
        var siblingRepoRoot = Path.GetFullPath(Path.Combine(solutionRoot, "..", "emma-test-plugin"));
        var projectPath = Path.Combine(siblingRepoRoot, "EMMA.TestPlugin.csproj");
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("EMMA test plugin project not found.", projectPath);
        }

        EnsureTestPluginBuilt(projectPath);

        var projectDir = Path.GetDirectoryName(projectPath) ?? throw new DirectoryNotFoundException(projectPath);
        var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        var candidates = new List<string>
        {
            Path.Combine(outputDir, "EMMA.TestPlugin")
        };

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(outputDir, "EMMA.TestPlugin.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Native EMMA test plugin entrypoint not found.", outputDir);
    }

    private static string ResolveSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solutionPath = Path.Combine(dir.FullName, "EMMA.sln");
            if (File.Exists(solutionPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate EMMA solution root.");
    }

    private static void CopyEntrypointToSandbox(string entrypointPath, string sandboxRoot, string pluginId)
    {
        var pluginRoot = Path.Combine(sandboxRoot, pluginId);
        Directory.CreateDirectory(pluginRoot);
        var sourceDir = Path.GetDirectoryName(entrypointPath) ?? throw new DirectoryNotFoundException(entrypointPath);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(pluginRoot, Path.GetFileName(file));
            File.Copy(file, target, true);
        }

        var destination = Path.Combine(pluginRoot, Path.GetFileName(entrypointPath));
        if (!File.Exists(destination))
        {
            File.Copy(entrypointPath, destination, true);
        }

        TryMakeExecutable(destination);
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
        }
    }

    private static void EnsureTestPluginBuilt(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" -c Debug -f net10.0",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start dotnet build.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet build failed.\n{output}\n{error}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
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

[CollectionDefinition("Embedded AspNet Plugin Integration", DisableParallelization = true)]
public sealed class EmbeddedAspNetPluginIntegrationCollection
{
}