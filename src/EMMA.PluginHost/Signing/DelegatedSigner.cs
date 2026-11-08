namespace EMMA.PluginHost.Signing;

/// <summary>A signing key a repository's root key has authorized for a scope of plugins.</summary>
/// <param name="KeyId">The key identifier plugins name in their signature.</param>
/// <param name="PublicKeyPem">The RSA public key as a <c>PUBLIC KEY</c> PEM block.</param>
/// <param name="Scopes">Plugin ids it may sign: an exact id, or <c>prefix.*</c> for everything under a prefix.</param>
/// <param name="ValidFrom">The start of its validity window.</param>
/// <param name="ValidUntil">The end of its validity window.</param>
/// <param name="Status">Whether it may currently sign.</param>
public sealed record DelegatedSigner(
    string KeyId,
    string PublicKeyPem,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    DelegationStatus Status);
