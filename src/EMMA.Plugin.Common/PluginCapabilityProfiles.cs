namespace EMMA.Plugin.Common;

public enum PluginCapabilityProfile
{
    PagedOnly,
    PagedAndVideo,
    PagedVideoAudio,
}

public static class PluginCapabilityProfiles
{
    public static CapabilityItem[] Create(PluginCapabilityProfile profile)
    {
        return profile switch
        {
            PluginCapabilityProfile.PagedOnly =>
            [
                new CapabilityItem("health", ["paged"], ["handshake", "capabilities", "search", "invoke"]),
                new CapabilityItem("search", ["paged"], ["search", "invoke"]),
                new CapabilityItem("paged-navigation", ["paged"], ["chapters", "page", "pages", "invoke"]),
                new CapabilityItem("media-operation", ["paged"], ["invoke"]),
            ],
            PluginCapabilityProfile.PagedAndVideo =>
            [
                new CapabilityItem("health", ["paged", "video"], ["handshake", "capabilities", "search", "invoke"]),
                new CapabilityItem("search", ["paged", "video"], ["search", "invoke"]),
                new CapabilityItem("paged-navigation", ["paged", "video"], ["chapters", "page", "pages", "invoke"]),
                new CapabilityItem("media-operation", ["paged", "video"], ["invoke", "video-streams", "video-segment"]),
            ],
            _ =>
            [
                new CapabilityItem("health", ["paged", "video", "audio"], ["handshake", "capabilities", "search", "invoke"]),
                new CapabilityItem("search", ["paged", "video", "audio"], ["search", "invoke"]),
                new CapabilityItem("paged-navigation", ["paged", "video"], ["chapters", "page", "pages", "invoke"]),
                new CapabilityItem("media-operation", ["paged", "video", "audio"], ["invoke", "video-streams", "video-segment"]),
            ],
        };
    }
}