namespace EMMA.Domain;

/// <summary>A filter value chosen by the user. Encoding per <see cref="FilterType"/>.</summary>
/// <param name="Id">The filter id from the plugin's declared filters.</param>
/// <param name="Values">The chosen values.</param>
public sealed record FilterValue(string Id, IReadOnlyList<string> Values);
