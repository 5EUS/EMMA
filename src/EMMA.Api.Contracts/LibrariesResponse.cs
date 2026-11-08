namespace EMMA.Api.Contracts;

/// <summary>The caller's libraries.</summary>
/// <param name="Libraries">The default library first, then the others by name.</param>
public sealed record LibrariesResponse(
    IReadOnlyList<LibrarySummaryDto> Libraries);
