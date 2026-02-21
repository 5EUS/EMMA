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

        var loader = new PluginManifestLoader(
            Options.Create(new EMMA.PluginHost.Configuration.PluginHostOptions
            {
                ManifestDirectory = tempRoot
            }),
            NullLogger<PluginManifestLoader>.Instance);

        var manifests = await loader.LoadManifestsAsync(CancellationToken.None);

        Assert.Single(manifests);
        Assert.Equal("demo", manifests[0].Id);
    }
}
