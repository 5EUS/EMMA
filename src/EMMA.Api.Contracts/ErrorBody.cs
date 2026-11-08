namespace EMMA.Api.Contracts;

/// <summary>The error inside an error response.</summary>
/// <param name="Code">A stable machine-readable code.</param>
/// <param name="Message">A message that is safe to show.</param>
/// <param name="RetryAfterMs">When to retry, in milliseconds, if known.</param>
public sealed record ErrorBody(
    string Code,
    string Message,
    long? RetryAfterMs);
