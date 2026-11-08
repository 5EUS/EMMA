namespace EMMA.Native.Requests;

/// <summary>Searches saved nodes by title.</summary>
/// <param name="Text">The text a title must contain.</param>
/// <param name="Library">One library, or absent for every library.</param>
/// <param name="Limit">The most entries to return.</param>
internal sealed record LibrarySearchCall(
    string? Text,
    string? Library,
    int? Limit);
