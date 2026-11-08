namespace EMMA.Domain;

/// <summary>A node a user saved to their library.</summary>
/// <param name="User">The owner.</param>
/// <param name="Node">The saved node, usually a series-like container.</param>
/// <param name="AddedAt">When it was added.</param>
public sealed record LibraryEntry(UserId User, NodeRef Node, DateTimeOffset AddedAt);
