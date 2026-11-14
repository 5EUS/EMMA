using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>A slice of a stored child listing.</summary>
/// <param name="Children">The children's ids in order, starting at the requested offset.</param>
/// <param name="Total">How many children the whole listing has.</param>
public sealed record StoredListing(IReadOnlyList<LocalNodeId> Children, int Total);
