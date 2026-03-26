#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using LibraryWorld;
using LibraryWorld.wit.exports.emma.plugin;
using Xunit;

namespace EMMA.Tests.PluginHost;

/// <summary>
/// Validation test demonstrating typed component exports working correctly.
/// This proves the typed ABI is functional and can be called directly or through CLI.
/// </summary>
public class WasmTypedComponentTest
{
    [Fact]
    public void ValidateTypedExports()
    {
        // Test 1: Handshake
        var handshake = EMMA.TestPlugin.WasmTypedExports.PluginImpl.Handshake();
        Assert.Equal("1.0.0", handshake.version);
        Assert.Equal("EMMA test wasm component ready", handshake.message);

        // Test 2: Capabilities
        var capabilities = EMMA.TestPlugin.WasmTypedExports.PluginImpl.Capabilities();
        Assert.NotEmpty(capabilities);
        var healthCap = capabilities.FirstOrDefault(c => c.name == "health");
        Assert.NotNull(healthCap);
        Assert.Contains("paged", healthCap.mediaTypes);
        Assert.Contains("invoke", healthCap.operations);

        // Test 3: Generic invoke with search
        var searchRequest = new IPlugin.MediaOperationRequest(
            "search",
            null,
            null,
            JsonSerializer.Serialize(new { query = "test" }),
            null);
        
        // This validates the invoke method signature is correct
        // Actual search would require prefetched payload
        var ex = Assert.Throws<WitException<IPlugin.OperationError>>(
            () => EMMA.TestPlugin.WasmTypedExports.PluginImpl.Invoke(searchRequest));
        
        // Expected: missing prefetched payload
        Assert.Equal(IPlugin.OperationError.Tags.Failed, ex.Value.Tag);
    }
}
#endif
