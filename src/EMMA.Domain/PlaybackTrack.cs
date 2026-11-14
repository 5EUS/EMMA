namespace EMMA.Domain;

/// <summary>One thing a player can pick: a stream or quality, a subtitle file or an audio track.</summary>
/// <param name="Role">The role it was resolved for; with <paramref name="Index"/> this is how a client asks for its bytes.</param>
/// <param name="Index">Its position among the node's assets for the role.</param>
/// <param name="Mime">The payload type.</param>
/// <param name="Size">The size in bytes, when known.</param>
/// <param name="Label">A name to show, when the plugin gave one.</param>
/// <param name="Language">A BCP 47 tag, when the plugin gave one.</param>
/// <param name="Quality">A quality label such as <c>1080p</c>.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Bitrate">Bits per second.</param>
/// <param name="IsDefault">Whether the plugin marked it as the one to use first.</param>
/// <param name="IsLive">Whether it is a live stream.</param>
/// <param name="DrmScheme">The DRM scheme, or <see langword="null"/> when the media is not protected.</param>
/// <param name="DrmLicenseUrl">Where a license is asked for.</param>
/// <param name="DurationMs">The length in milliseconds.</param>
public sealed record PlaybackTrack(
    AssetRole Role,
    int Index,
    string Mime,
    long? Size,
    string? Label,
    string? Language,
    string? Quality,
    int? Width,
    int? Height,
    long? Bitrate,
    bool IsDefault,
    bool IsLive,
    string? DrmScheme,
    Uri? DrmLicenseUrl,
    long? DurationMs);
