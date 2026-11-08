namespace EMMA.Api.Contracts;

/// <summary>A search filter the plugin declares.</summary>
/// <param name="Id">The filter id.</param>
/// <param name="Label">The display label.</param>
/// <param name="Type"><c>toggle</c>, <c>single_choice</c>, <c>multi_choice</c>, <c>number_range</c>, <c>text</c> or <c>lookup</c>.</param>
/// <param name="Kinds">Kinds it applies to; empty means all.</param>
/// <param name="Required">Whether a value must be supplied.</param>
/// <param name="Hidden">Whether clients should hide it by default.</param>
/// <param name="Group">A grouping name.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Choices">Options for choice filters.</param>
/// <param name="Range">Bounds for range filters.</param>
/// <param name="Lookup">Completion behaviour for lookup filters.</param>
internal sealed record FilterDto(
    string Id,
    string Label,
    string Type,
    IReadOnlyList<string> Kinds,
    bool Required,
    bool Hidden,
    string? Group,
    string? Description,
    IReadOnlyList<ChoiceDto> Choices,
    RangeDto? Range,
    LookupDto? Lookup);
