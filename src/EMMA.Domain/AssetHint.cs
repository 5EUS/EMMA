namespace EMMA.Domain;

/// <summary>
/// The one preference a client can pass when it asks for an asset: a quality such as <c>1080p</c>, sent to the
/// plugin as the <c>emma.quality</c> hint. A plugin is free to ignore it; the host never interprets the text.
/// </summary>
public static class AssetHint
{
    /// <summary>The longest hint, in characters.</summary>
    public const int MaxLength = 64;

    /// <summary>The key of the hint the plugin receives.</summary>
    public const string QualityKey = "emma.quality";

    /// <summary>Checks a hint.</summary>
    /// <param name="hint">The hint, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when there is no hint or it is 1 to <see cref="MaxLength"/> characters with no control characters.</returns>
    public static bool IsValid(string? hint) =>
        hint is null || (hint.Length is >= 1 and <= MaxLength && !hint.Any(char.IsControl));

    /// <summary>Turns a hint into what a resolve request carries.</summary>
    /// <param name="hint">A hint that passed <see cref="IsValid"/>, or <see langword="null"/>.</param>
    /// <returns>No hints, or the quality hint.</returns>
    public static IReadOnlyList<Prop> ToProps(string? hint) =>
        hint is null ? [] : [new Prop(QualityKey, PropValue.FromText(hint))];
}
