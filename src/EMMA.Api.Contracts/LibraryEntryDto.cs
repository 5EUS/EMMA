namespace EMMA.Api.Contracts;

/// <summary>A saved node.</summary>
/// <param name="Node">The node reference.</param>
/// <param name="Library">The name of the library it is in.</param>
/// <param name="AddedAt">When it was added to that library.</param>
public sealed record LibraryEntryDto(
    string Node,
    string Library,
    DateTimeOffset AddedAt);
