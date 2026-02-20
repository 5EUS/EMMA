namespace EMMA.Domain;

/// <summary>
/// Domain-level error payload for consistent reporting.
/// </summary>
public sealed record DomainError(string Code, string Message);
