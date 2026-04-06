using EMMA.Plugin.Common;
using Xunit;

namespace EMMA.Tests.PluginHost;

public sealed class PluginVideoSegmentFileNamingTests
{
    [Fact]
    public void ResolveSegmentExtension_PreservesKnownMediaExtensions()
    {
        var ts = PluginVideoSegmentFileNaming.ResolveSegmentExtension(new Uri("https://cdn.example/video/000001.ts"));
        var m4s = PluginVideoSegmentFileNaming.ResolveSegmentExtension(new Uri("https://cdn.example/video/000001.m4s"));

        Assert.Equal(".ts", ts);
        Assert.Equal(".m4s", m4s);
    }

    [Fact]
    public void ResolveSegmentExtension_FallsBackToTsForUnknownOrMissingExtension()
    {
        var unknown = PluginVideoSegmentFileNaming.ResolveSegmentExtension(new Uri("https://cdn.example/video/000001.segment"));
        var missing = PluginVideoSegmentFileNaming.ResolveSegmentExtension(new Uri("https://cdn.example/video/000001"));

        Assert.Equal(".ts", unknown);
        Assert.Equal(".ts", missing);
    }
}
