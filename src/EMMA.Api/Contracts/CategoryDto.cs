namespace EMMA.Api.Contracts;

/// <summary>A group of settings.</summary>
/// <param name="Id">The category id.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional description.</param>
internal sealed record CategoryDto(
    string Id,
    string Label,
    string? Description);
