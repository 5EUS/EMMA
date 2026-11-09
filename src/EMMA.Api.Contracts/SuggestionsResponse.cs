namespace EMMA.Api.Contracts;

/// <summary>Suggested values for a lookup filter.</summary>
/// <param name="Suggestions">The suggestions, best first; each carries the value to send back in a search filter.</param>
public sealed record SuggestionsResponse(
    IReadOnlyList<ChoiceDto> Suggestions);
