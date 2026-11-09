namespace EMMA.Native.Requests;

/// <summary>A request for suggested values of a plugin's lookup filter.</summary>
/// <param name="Plugin">The plugin id.</param>
/// <param name="Filter">The lookup filter id.</param>
/// <param name="Text">The partial text.</param>
/// <param name="Limit">The most suggestions wanted.</param>
internal sealed record SuggestCall(
    string? Plugin,
    string? Filter,
    string? Text,
    int? Limit);
