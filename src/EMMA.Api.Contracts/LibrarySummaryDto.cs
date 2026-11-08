namespace EMMA.Api.Contracts;

/// <summary>One of the caller's libraries.</summary>
/// <param name="Name">The name, as it was created.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Count">How many nodes are saved in it.</param>
/// <param name="IsDefault">Whether it is the default library, which cannot be deleted.</param>
public sealed record LibrarySummaryDto(
    string Name,
    DateTimeOffset CreatedAt,
    int Count,
    bool IsDefault);
