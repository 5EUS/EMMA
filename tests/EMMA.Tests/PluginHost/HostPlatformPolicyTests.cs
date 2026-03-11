using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Platform;

namespace EMMA.Tests.PluginHost;

public sealed class HostPlatformPolicyTests
{
    [Fact]
    public void UsesInternalNativeWasmLibrary_RespectsInternalOverride()
    {
        var options = new PluginHostOptions
        {
            NativeWasmLibraryMode = NativeWasmLibraryMode.Internal
        };

        Assert.True(HostPlatformPolicy.UsesInternalNativeWasmLibrary(options));
    }

    [Fact]
    public void UsesInternalNativeWasmLibrary_RespectsExternalOverride()
    {
        var options = new PluginHostOptions
        {
            NativeWasmLibraryMode = NativeWasmLibraryMode.External
        };

        Assert.False(HostPlatformPolicy.UsesInternalNativeWasmLibrary(options));
    }

    [Fact]
    public void UsesInternalNativeWasmLibrary_AutoFollowsPlatformDefaultPolicy()
    {
        var options = new PluginHostOptions
        {
            NativeWasmLibraryMode = NativeWasmLibraryMode.Auto
        };

        var expected = HostPlatformPolicy.Current == HostPlatform.AppleMobile;
        Assert.Equal(expected, HostPlatformPolicy.UsesInternalNativeWasmLibrary(options));
    }
}
