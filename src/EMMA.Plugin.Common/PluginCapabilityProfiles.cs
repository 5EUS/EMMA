namespace EMMA.Plugin.Common;

/// <summary>
/// Describes the default capability set exposed by a plugin transport profile.
/// </summary>
public enum PluginCapabilityProfile
{
    /// <summary>
    /// Supports paged media operations only.
    /// </summary>
    PagedOnly,

    /// <summary>
    /// Supports paged and video media operations.
    /// </summary>
    PagedAndVideo,

    /// <summary>
    /// Supports paged, video, and audio media operations.
    /// </summary>
    PagedVideoAudio,
}

/// <summary>
/// Creates standard capability declarations for common plugin media profiles.
/// </summary>
public static class PluginCapabilityProfiles
{
    /// <summary>
    /// Creates the default capability declaration set for the requested plugin profile.
    /// </summary>
    /// <param name="profile">The capability profile that determines which media types and operations are exposed.</param>
    /// <returns>The capability items advertised for the selected profile.</returns>
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