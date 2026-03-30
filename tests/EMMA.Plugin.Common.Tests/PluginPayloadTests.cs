using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginPayloadTests
{
    [Fact]
    public void NormalizePayload_StripsPrefixBeforeJson()
    {
        var normalized = PluginPayload.NormalizePayload("noise before {\"ok\":true}");

        Assert.Equal("{\"ok\":true}", normalized);
    }

    [Fact]
    public void ResolvePayload_PrefersProvidedPayload()
    {
        var result = PluginPayload.ResolvePayload("{\"provided\":1}", () => "{\"fallback\":2}");

        Assert.Equal("{\"provided\":1}", result);
    }

    [Fact]
    public void ResolvePayload_UsesFallbackWhenProvidedIsEmpty()
    {
        var result = PluginPayload.ResolvePayload("", () => "{\"fallback\":2}");

        Assert.Equal("{\"fallback\":2}", result);
    }

    [Fact]
    public void ResolvePayload_ReturnsEmptyWhenNoJsonAvailable()
    {
        var result = PluginPayload.ResolvePayload("", () => "not json");

        Assert.Equal(string.Empty, result);
    }
}
