namespace EMMA.Contracts;

/// <summary>
/// Contextual information about a request, such as correlation ID and deadline, that can be used for logging, tracing, and enforcing timeouts.
/// </summary>
/// <param name="CorrelationId"></param>
/// <param name="DeadlineUtc"></param>
public sealed record RequestContext(
    string CorrelationId,
    DateTimeOffset DeadlineUtc);
