namespace EMMA.Domain;

/// <summary>Result counts per value for one filter the plugin can facet.</summary>
/// <param name="FilterId">The filter the counts belong to.</param>
/// <param name="Counts">The counts.</param>
public sealed record Facet(string FilterId, IReadOnlyList<FacetCount> Counts);
