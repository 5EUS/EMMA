namespace EMMA.Api.Contracts;

/// <summary>Creates a library.</summary>
/// <param name="Name">The name: 1 to 64 characters, not already used (case is ignored).</param>
public sealed record CreateLibraryRequest(
    string? Name);
