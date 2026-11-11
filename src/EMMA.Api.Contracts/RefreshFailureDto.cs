namespace EMMA.Api.Contracts;

/// <summary>A saved node that could not be refreshed.</summary>
/// <param name="Node">The node reference.</param>
/// <param name="Error">Why, in the same vocabulary as every other error.</param>
public sealed record RefreshFailureDto(
    string Node,
    ErrorBody Error);
