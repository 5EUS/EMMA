namespace EMMA.Api.Contracts;

/// <summary>A saved node.</summary>
/// <param name="Node">The node reference.</param>
/// <param name="AddedAt">When it was added.</param>
internal sealed record LibraryEntryDto(
    string Node,
    DateTimeOffset AddedAt);
