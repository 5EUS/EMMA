namespace EMMA.Domain;

/// <summary>Groups settings in a settings screen.</summary>
/// <param name="Id">The category id settings refer to.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional description.</param>
public sealed record SettingCategory(string Id, string Label, string? Description);
