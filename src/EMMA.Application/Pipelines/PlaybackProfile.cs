using System.Collections.Frozen;

using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Reads the video and audio profiles (see <see cref="PlaybackVocabulary"/>): checks that the well-known props on an
/// asset have the type the contract gives them, and turns assets into the typed tracks and chapters a player uses.
/// A value of the wrong type is a plugin bug, so it is rejected like any other contract violation rather than guessed at.
/// </summary>
internal static class PlaybackProfile
{
    private const int MaxLabelLength = 256;
    private const int MaxLanguageLength = 35;

    private static readonly FrozenDictionary<string, PropValueKind> Kinds = new Dictionary<string, PropValueKind>(StringComparer.Ordinal)
    {
        [PlaybackVocabulary.Quality] = PropValueKind.Text,
        [PlaybackVocabulary.Language] = PropValueKind.Text,
        [PlaybackVocabulary.Label] = PropValueKind.Text,
        [PlaybackVocabulary.Width] = PropValueKind.WholeNumber,
        [PlaybackVocabulary.Height] = PropValueKind.WholeNumber,
        [PlaybackVocabulary.Bitrate] = PropValueKind.WholeNumber,
        [PlaybackVocabulary.Live] = PropValueKind.Flag,
        [PlaybackVocabulary.Default] = PropValueKind.Flag,
        [PlaybackVocabulary.DrmScheme] = PropValueKind.Text,
        [PlaybackVocabulary.DrmLicenseUrl] = PropValueKind.Url,
        [PlaybackVocabulary.DurationMs] = PropValueKind.WholeNumber,
        [PlaybackVocabulary.StartMs] = PropValueKind.WholeNumber,
        [PlaybackVocabulary.EndMs] = PropValueKind.WholeNumber,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Checks the well-known props of an asset.</summary>
    /// <param name="props">The asset's props.</param>
    /// <returns>A description of the first problem, or <see langword="null"/> when the props are fine.</returns>
    public static string? Problem(IReadOnlyList<Prop> props)
    {
        foreach (Prop prop in props)
        {
            if (!Kinds.TryGetValue(prop.Key, out PropValueKind expected))
            {
                continue;
            }

            if (prop.Value.Kind != expected)
            {
                return $"returned an asset whose '{prop.Key}' is not {Describe(expected)}";
            }

            string? bad = BadValue(prop);
            if (bad is not null)
            {
                return $"returned an asset whose '{prop.Key}' {bad}";
            }
        }

        return null;
    }

    /// <summary>Turns the assets of one role into tracks.</summary>
    /// <param name="role">The role they were resolved for.</param>
    /// <param name="assets">The resolved assets, in the plugin's order.</param>
    /// <returns>One track per asset, in the same order.</returns>
    public static IReadOnlyList<PlaybackTrack> Tracks(AssetRole role, IReadOnlyList<Asset> assets) =>
        [.. assets.Select((asset, index) => ToTrack(role, index, asset))];

    /// <summary>Turns the assets of the <c>chapters</c> role into chapter markers.</summary>
    /// <param name="plugin">The plugin that returned them, for the message.</param>
    /// <param name="assets">The resolved assets.</param>
    /// <returns>The markers in the plugin's order, or the first contract violation.</returns>
    public static Outcome<IReadOnlyList<PlaybackChapter>> Chapters(PluginId plugin, IReadOnlyList<Asset> assets)
    {
        List<PlaybackChapter> chapters = new(assets.Count);
        for (int index = 0; index < assets.Count; index++)
        {
            Asset asset = assets[index];
            if (Whole(asset.Props, PlaybackVocabulary.StartMs) is not { } start)
            {
                return Outcome.Failure<IReadOnlyList<PlaybackChapter>>(ErrorCode.Internal, $"Plugin {plugin} returned a chapter marker without '{PlaybackVocabulary.StartMs}'.");
            }

            long? end = Whole(asset.Props, PlaybackVocabulary.EndMs);
            if (end < start)
            {
                return Outcome.Failure<IReadOnlyList<PlaybackChapter>>(ErrorCode.Internal, $"Plugin {plugin} returned a chapter marker that ends before it starts.");
            }

            string title = Text(asset.Props, PlaybackVocabulary.Label) ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Chapter {index + 1}");
            chapters.Add(new PlaybackChapter(index, start, end, title));
        }

        return Outcome.Success<IReadOnlyList<PlaybackChapter>>(chapters);
    }

    private static PlaybackTrack ToTrack(AssetRole role, int index, Asset asset)
    {
        string? scheme = Text(asset.Props, PlaybackVocabulary.DrmScheme);
        bool protectedMedia = scheme is not null && !string.Equals(scheme, PlaybackVocabulary.NoDrm, StringComparison.OrdinalIgnoreCase);
        return new PlaybackTrack(
            role,
            index,
            asset.Mime,
            asset.Size,
            Text(asset.Props, PlaybackVocabulary.Label),
            Text(asset.Props, PlaybackVocabulary.Language),
            Text(asset.Props, PlaybackVocabulary.Quality),
            ToInt(Whole(asset.Props, PlaybackVocabulary.Width)),
            ToInt(Whole(asset.Props, PlaybackVocabulary.Height)),
            Whole(asset.Props, PlaybackVocabulary.Bitrate),
            Flag(asset.Props, PlaybackVocabulary.Default),
            Flag(asset.Props, PlaybackVocabulary.Live),
            protectedMedia ? scheme : null,
            protectedMedia ? LicenseUrl(asset.Props) : null,
            Whole(asset.Props, PlaybackVocabulary.DurationMs));
    }

    private static Uri? LicenseUrl(IReadOnlyList<Prop> props) =>
        Find(props, PlaybackVocabulary.DrmLicenseUrl) is { Kind: PropValueKind.Url } url ? new Uri(url.Text) : null;

    private static string? BadValue(Prop prop) => prop.Key switch
    {
        PlaybackVocabulary.Width or PlaybackVocabulary.Height or PlaybackVocabulary.Bitrate
            or PlaybackVocabulary.DurationMs or PlaybackVocabulary.StartMs or PlaybackVocabulary.EndMs
            when prop.Value.WholeNumber < 0 => "is negative",
        PlaybackVocabulary.Label when prop.Value.Text.Length > MaxLabelLength => "is too long",
        PlaybackVocabulary.Language when prop.Value.Text.Length is 0 or > MaxLanguageLength => "is not a language tag",
        PlaybackVocabulary.DrmLicenseUrl when !IsWebUrl(prop.Value.Text) => "is not a web address",
        _ => null,
    };

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? url)
        && (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal));

    private static string Describe(PropValueKind kind) => kind switch
    {
        PropValueKind.Text => "text",
        PropValueKind.WholeNumber => "a whole number",
        PropValueKind.Flag => "a flag",
        PropValueKind.Url => "a URL",
        _ => "the expected type",
    };

    private static PropValue? Find(IReadOnlyList<Prop> props, string key)
    {
        return props.Where(prop => string.Equals(prop.Key, key, StringComparison.Ordinal)).Select(prop => prop.Value).FirstOrDefault();
    }

    private static string? Text(IReadOnlyList<Prop> props, string key) =>
        Find(props, key) is { Kind: PropValueKind.Text } value && value.Text.Length > 0 ? value.Text : null;

    private static long? Whole(IReadOnlyList<Prop> props, string key) =>
        Find(props, key) is { Kind: PropValueKind.WholeNumber } value ? value.WholeNumber : null;

    private static bool Flag(IReadOnlyList<Prop> props, string key) =>
        Find(props, key) is { Kind: PropValueKind.Flag } value && value.Flag;

    private static int? ToInt(long? value) => value is { } number ? (int)Math.Min(number, int.MaxValue) : null;
}
