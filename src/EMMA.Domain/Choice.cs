namespace EMMA.Domain;

/// <summary>A labelled option for a filter or a sort order.</summary>
/// <param name="Value">The value sent back to the plugin.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional longer description.</param>
/// <param name="IsDefault">Whether the option is preselected.</param>
public sealed record Choice(string Value, string Label, string? Description, bool IsDefault);
