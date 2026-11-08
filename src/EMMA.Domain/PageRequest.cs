namespace EMMA.Domain;

/// <summary>A cursor-paged request for the children of a node.</summary>
/// <param name="Cursor">The previous page's next cursor, or <see langword="null"/> for the first page.</param>
/// <param name="Limit">A hint for the page size.</param>
public sealed record PageRequest(string? Cursor = null, int? Limit = null);
