using EMMA.Plugin.AspNetCore;
using EMMA.Plugin.Common;

namespace EMMA.Tests;

public class PluginSdkControlOptionsExtensionsTests
{
    [Fact]
    public void ApplyManifestDefaults_ReplacesBudgetsDomainsAndPaths()
    {
        var options = new PluginSdkControlOptions
        {
            CpuBudgetMs = 1,
            MemoryMb = 2
        };
        options.Domains.Add("old.example");
        options.Paths.Add("/old");

        options.ApplyManifestDefaults(new PluginManifestDefaults(
            250,
            512,
            ["api.example", "cdn.example"],
            ["/data", "/cache"]));

        Assert.Equal(250, options.CpuBudgetMs);
        Assert.Equal(512, options.MemoryMb);
        Assert.Equal(["api.example", "cdn.example"], options.Domains);
        Assert.Equal(["/data", "/cache"], options.Paths);
    }

    [Fact]
    public void ApplyDefaults_ReplacesControlMessageCapabilitiesAndManifestValues()
    {
        var options = new PluginSdkControlOptions();
        options.Capabilities.Add("old-capability");

        options.ApplyDefaults(new PluginSdkControlDefaults(
            Message: "template ready",
            CpuBudgetMs: 300,
            MemoryMb: 1024,
            Capabilities: ["search", "pages"],
            Domains: ["api.example"],
            Paths: ["/var/tmp/plugin"]));

        Assert.Equal("template ready", options.Message);
        Assert.Equal(300, options.CpuBudgetMs);
        Assert.Equal(1024, options.MemoryMb);
        Assert.Equal(["search", "pages"], options.Capabilities);
        Assert.Equal(["api.example"], options.Domains);
        Assert.Equal(["/var/tmp/plugin"], options.Paths);
    }
}