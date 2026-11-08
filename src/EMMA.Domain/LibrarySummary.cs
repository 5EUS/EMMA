namespace EMMA.Domain;

/// <summary>One of a user's libraries and how many nodes it holds.</summary>
/// <param name="Name">The library's name.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Count">How many nodes are saved in it.</param>
public sealed record LibrarySummary(LibraryName Name, DateTimeOffset CreatedAt, int Count);
