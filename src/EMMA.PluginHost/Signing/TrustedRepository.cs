namespace EMMA.PluginHost.Signing;

/// <summary>A repository the host trusts, and the offline root keys it will accept delegation files from.</summary>
/// <param name="Id">The repository id.</param>
/// <param name="RootKeys">Root public keys (<c>PUBLIC KEY</c> PEM) by key id.</param>
public sealed record TrustedRepository(string Id, IReadOnlyDictionary<string, string> RootKeys);
