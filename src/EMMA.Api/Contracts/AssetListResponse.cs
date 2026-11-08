namespace EMMA.Api.Contracts;

/// <summary>The assets of a node for a role.</summary>
/// <param name="Assets">The assets.</param>
internal sealed record AssetListResponse(
    IReadOnlyList<AssetDto> Assets);
