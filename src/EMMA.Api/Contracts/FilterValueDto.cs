namespace EMMA.Api.Contracts;

/// <summary>A chosen filter value.</summary>
/// <param name="Id">The filter id.</param>
/// <param name="Values">The chosen values.</param>
internal sealed record FilterValueDto(
    string? Id,
    IReadOnlyList<string>? Values);
