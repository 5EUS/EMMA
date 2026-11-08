namespace EMMA.Api.Contracts;

/// <summary>The body of every failed request.</summary>
/// <param name="Error">The error.</param>
internal sealed record ErrorResponse(
    ErrorBody Error);
