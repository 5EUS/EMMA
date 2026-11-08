namespace EMMA.Api.Contracts;

/// <summary>Nodes fetched by reference.</summary>
/// <param name="Nodes">The nodes found, in the order requested.</param>
internal sealed record NodeListResponse(
    IReadOnlyList<NodeDto> Nodes);
