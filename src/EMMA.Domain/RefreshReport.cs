namespace EMMA.Domain;

/// <summary>What refreshing a library found.</summary>
/// <param name="Checked">How many saved nodes were looked at, including the ones that failed.</param>
/// <param name="Updated">The nodes that have new children.</param>
/// <param name="Failed">The nodes that could not be refreshed; one failing does not stop the others.</param>
public sealed record RefreshReport(
    int Checked,
    IReadOnlyList<NodeUpdate> Updated,
    IReadOnlyList<NodeRefreshFailure> Failed);
