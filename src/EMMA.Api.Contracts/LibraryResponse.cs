namespace EMMA.Api.Contracts;

/// <summary>The caller's library.</summary>
/// <param name="Entries">Most recently added first.</param>
public sealed record LibraryResponse(
    IReadOnlyList<LibraryEntryDto> Entries);
