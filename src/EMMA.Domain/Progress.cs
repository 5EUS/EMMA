namespace EMMA.Domain;

/// <summary>A user's position within a node. One record shape for every media type.</summary>
/// <param name="User">Whose progress it is.</param>
/// <param name="Node">The node the user is in: the chapter for paged media, the episode for video.</param>
/// <param name="Locator">The position within the node.</param>
/// <param name="Completed">Whether the user finished the node.</param>
/// <param name="UpdatedAt">When the progress was recorded.</param>
public sealed record Progress(
    UserId User,
    NodeRef Node,
    ProgressLocator Locator,
    bool Completed,
    DateTimeOffset UpdatedAt);
