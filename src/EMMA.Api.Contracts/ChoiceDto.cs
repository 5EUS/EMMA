namespace EMMA.Api.Contracts;

/// <summary>A labelled option.</summary>
/// <param name="Value">The value sent back to the plugin.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional longer description.</param>
/// <param name="IsDefault">Whether the option is preselected.</param>
public sealed record ChoiceDto(
    string Value,
    string Label,
    string? Description,
    bool IsDefault);
