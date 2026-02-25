using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EMMA.Tests.PluginHost;

public sealed class PluginManifestLoaderTests
{
    [Fact]
    public async Task LoadManifestsAsync_LoadsValidManifest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");

        await File.WriteAllTextAsync(manifestPath, "{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"http://localhost:5005\"\n  }\n}");

        var options = Options.Create(new EMMA.PluginHost.Configuration.PluginHostOptions
        {
            ManifestDirectory = tempRoot
        });
        var sanitizer = new PluginPermissionSanitizer(options, NullLogger<PluginPermissionSanitizer>.Instance);
        var loader = new PluginManifestLoader(
            options,
            sanitizer,
            NullLogger<PluginManifestLoader>.Instance);

        var manifests = await loader.LoadManifestsAsync(CancellationToken.None);

        Assert.Single(manifests);
        Assert.Equal("demo", manifests[0].Id);
    }

    [Fact]
    public async Task LoadManifestsAsync_SanitizesPermissionsPaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var manifestPath = Path.Combine(tempRoot, "demo.plugin.json");

        await File.WriteAllTextAsync(manifestPath, "{\n  \"id\": \"demo\",\n  \"name\": \"Demo Plugin\",\n  \"version\": \"1.0.0\",\n  \"entry\": {\n    \"protocol\": \"grpc\",\n    \"endpoint\": \"http://localhost:5005\"\n  },\n  \"permissions\": {\n    \"paths\": [\"/etc\", \"../escape\", \"data\", \"data\", \"DATA\", \"\"]\n  }\n}");

        var options = Options.Create(new EMMA.PluginHost.Configuration.PluginHostOptions
        {
            ManifestDirectory = tempRoot,
            SandboxRootDirectory = Path.Combine(tempRoot, "sandbox")
        });
        var sanitizer = new PluginPermissionSanitizer(options, NullLogger<PluginPermissionSanitizer>.Instance);
        var loader = new PluginManifestLoader(
            options,
            sanitizer,
            NullLogger<PluginManifestLoader>.Instance);

        var manifests = await loader.LoadManifestsAsync(CancellationToken.None);

        Assert.Single(manifests);
        var paths = manifests[0].Permissions?.Paths ?? [];
        var expectedPath = Path.GetFullPath(Path.Combine(options.Value.SandboxRootDirectory, "demo", "data"));
        Assert.Single(paths);
        Assert.Equal(expectedPath, paths[0]);
    }
}
