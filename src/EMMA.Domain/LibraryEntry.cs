namespace EMMA.Domain;

/// <summary>A node a user saved to one of their libraries. A node is in at most one library per user.</summary>
/// <param name="User">The owner.</param>
/// <param name="Library">The library it is in.</param>
/// <param name="Node">The saved node, usually a series-like container.</param>
/// <param name="AddedAt">When it was added to the library it is in now; moving it keeps the date.</param>
public sealed record LibraryEntry(UserId User, LibraryName Library, NodeRef Node, DateTimeOffset AddedAt);
