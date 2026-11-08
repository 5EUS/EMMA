using EMMA.Domain;
using EMMA.PluginConformance;
using EMMA.PluginHost.Instances;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Runtime;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.WasmRuntime.Tests;

/// <summary>The Wasm transport passes the same contract suite as the in-memory reference, against the real fixture component.</summary>
/// <param name="runtime">The shared native runtime.</param>
public sealed class WasmInstanceConformanceTests(RuntimeFixture runtime) : PluginInstanceConformance, IClassFixture<RuntimeFixture>
{
    /// <inheritdoc/>
    protected override async Task<IPluginInstance> CreateAsync(bool searchAssist)
    {
        string path = NativeArtifacts.Plugin(searchAssist ? "fixture-assist" : "fixture");
        byte[] component = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(false);
        PluginManifest manifest = TestPackages.Manifest(component, sign: false);
        PluginLaunch launch = new(manifest, path, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        Outcome<IPluginInstance> opened = await new WasmPluginTransport(runtime.Host).OpenAsync(launch, TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.True(opened.IsSuccess, opened.Error?.Message);
        return opened.Value;
    }
}
