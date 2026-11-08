namespace EMMA.Api.Contracts;

/// <summary>Saved nodes.</summary>
/// <param name="Entries">Most recently added first.</param>
public sealed record LibraryResponse(
    IReadOnlyList<LibraryEntryDto> Entries);
