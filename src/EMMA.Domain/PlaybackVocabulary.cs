namespace EMMA.Domain;

/// <summary>
/// The names the video and audio profiles (<c>emma.profile.video@1</c>, <c>emma.profile.audio@1</c>) use: the asset
/// roles a playable node resolves in and the well-known <c>emma.*</c> props that tell its assets apart. Nothing in the
/// core switches on a media type; these are the strings a client player and the host's playback view agree on.
/// </summary>
public static class PlaybackVocabulary
{
    /// <summary>The role of an adaptive manifest or a direct video file.</summary>
    public const string StreamRole = "stream";

    /// <summary>The role of a subtitle file. Several assets are several languages.</summary>
    public const string SubtitleRole = "subtitle";

    /// <summary>The role of audio: the track of an audio node, or an alternate track of a stream node.</summary>
    public const string AudioRole = "audio";

    /// <summary>The role of chapter markers: one asset per marker.</summary>
    public const string ChaptersRole = "chapters";

    /// <summary>Text: a quality label such as <c>1080p</c>.</summary>
    public const string Quality = "emma.quality";

    /// <summary>Text: a BCP 47 language tag.</summary>
    public const string Language = "emma.language";

    /// <summary>Text: a name to show for a track or chapter.</summary>
    public const string Label = "emma.label";

    /// <summary>Integer: width in pixels.</summary>
    public const string Width = "emma.width";

    /// <summary>Integer: height in pixels.</summary>
    public const string Height = "emma.height";

    /// <summary>Integer: bits per second.</summary>
    public const string Bitrate = "emma.bitrate";

    /// <summary>Flag: a live stream that has no end.</summary>
    public const string Live = "emma.live";

    /// <summary>Flag: the track to use when the user has not chosen one.</summary>
    public const string Default = "emma.default";

    /// <summary>Text: the DRM scheme (<c>widevine</c>, <c>fairplay</c>, <c>playready</c>), or <c>none</c>.</summary>
    public const string DrmScheme = "emma.drm.scheme";

    /// <summary>URL: where a client asks for a license.</summary>
    public const string DrmLicenseUrl = "emma.drm.license-url";

    /// <summary>Integer: length of the media in milliseconds.</summary>
    public const string DurationMs = "emma.duration-ms";

    /// <summary>Integer: where a chapter starts, in milliseconds.</summary>
    public const string StartMs = "emma.start-ms";

    /// <summary>Integer: where a chapter ends, in milliseconds.</summary>
    public const string EndMs = "emma.end-ms";

    /// <summary>The value of <see cref="DrmScheme"/> that means the media is not protected.</summary>
    public const string NoDrm = "none";
}
