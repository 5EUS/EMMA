namespace EMMA.Runtime;

/// <summary>A trusted plugin repository in a configuration document.</summary>
/// <param name="Id">The repository id.</param>
/// <param name="RootKeys">The repository's root public keys (PEM) by key id.</param>
internal sealed record RepositoryDocument(string? Id, Dictionary<string, string>? RootKeys);
