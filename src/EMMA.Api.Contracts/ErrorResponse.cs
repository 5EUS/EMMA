namespace EMMA.Api.Contracts;

/// <summary>The body of every failed request.</summary>
/// <param name="Error">The error.</param>
public sealed record ErrorResponse(
    ErrorBody Error);
