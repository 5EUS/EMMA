namespace EMMA.PluginHost.Signing;

/// <summary>
/// A repository's list of authorized signers, signed by its offline root key. Compromise of
/// one delegated key is contained to its scopes, and revoking it needs a new bundle, not
/// re-signing the repository.
/// </summary>
/// <param name="RepositoryId">The repository this bundle speaks for.</param>
/// <param name="Version">A number that only ever increases; the host refuses a lower one than it has accepted.</param>
/// <param name="IssuedAt">When the root key signed it.</param>
/// <param name="RootKeyId">Which trusted root key signed it.</param>
/// <param name="Signature">The base64 root signature.</param>
/// <param name="Delegations">The authorized signers.</param>
public sealed record DelegationBundle(
    string RepositoryId,
    long Version,
    DateTimeOffset IssuedAt,
    string RootKeyId,
    string Signature,
    IReadOnlyList<DelegatedSigner> Delegations);
