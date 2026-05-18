using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginCapabilityProfilesTests
{
    [Fact]
    public void Create_PagedOnly_ExcludesVideoOperations()
    {
        var capabilities = PluginCapabilityProfiles.Create(PluginCapabilityProfile.PagedOnly);

        Assert.Contains(capabilities, capability => capability.name == "paged-navigation");
        Assert.DoesNotContain(capabilities.SelectMany(static capability => capability.operations ?? []), operation => operation == "video-streams");
        Assert.DoesNotContain(capabilities.SelectMany(static capability => capability.mediaTypes ?? []), mediaType => mediaType == "video");
    }

    [Fact]
    public void Create_PagedAndVideo_IncludesVideoOperations()
    {
        var capabilities = PluginCapabilityProfiles.Create(PluginCapabilityProfile.PagedAndVideo);

        Assert.Contains(capabilities.SelectMany(static capability => capability.mediaTypes ?? []), mediaType => mediaType == "video");
        Assert.Contains(capabilities.SelectMany(static capability => capability.operations ?? []), operation => operation == "video-streams");
        Assert.Contains(capabilities.SelectMany(static capability => capability.operations ?? []), operation => operation == "video-segment");
    }
}