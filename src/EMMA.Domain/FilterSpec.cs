namespace EMMA.Domain;

/// <summary>Declares a search filter so clients can render it without plugin-specific code.</summary>
/// <param name="Id">The filter id.</param>
/// <param name="Label">The display label.</param>
/// <param name="Type">How the filter is rendered and encoded.</param>
/// <param name="Kinds">Kinds the filter applies to; empty means all.</param>
/// <param name="Required">Whether a value must be supplied.</param>
/// <param name="Hidden">Whether clients should hide it by default.</param>
/// <param name="Group">Clients group filters with the same name together.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Choices">The options for choice filters.</param>
/// <param name="Range">The bounds for range filters.</param>
/// <param name="Lookup">The completion behaviour for lookup filters.</param>
public sealed record FilterSpec(
    string Id,
    string Label,
    FilterType Type,
    IReadOnlyList<NodeKind> Kinds,
    bool Required,
    bool Hidden,
    string? Group,
    string? Description,
    IReadOnlyList<Choice> Choices,
    RangeSpec? Range,
    LookupSpec? Lookup);
