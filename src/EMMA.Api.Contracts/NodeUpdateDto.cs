namespace EMMA.Api.Contracts;

/// <summary>A saved node that has new children.</summary>
/// <param name="Node">The saved node.</param>
/// <param name="NewChildren">The children the host had not seen before, in the order the plugin lists them.</param>
public sealed record NodeUpdateDto(
    NodeDto Node,
    IReadOnlyList<NodeDto> NewChildren);
