namespace EMMA.Api.Contracts;

/// <summary>The answer to emptying the caches.</summary>
/// <param name="Cleared">Always <see langword="true"/>; clearing an empty cache is not an error.</param>
public sealed record CacheClearedResponse(
    bool Cleared);
