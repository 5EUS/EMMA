namespace EMMA.PluginHost.Manifest;

/// <summary>A repository signature over the manifest.</summary>
/// <param name="Value">The base64 RSA signature.</param>
/// <param name="KeyId">The delegated signing key.</param>
/// <param name="RepositoryId">The repository that vouches for the plugin.</param>
/// <param name="IssuedAt">When it was signed.</param>
/// <param name="ExpiresAt">When it stops being valid.</param>
/// <param name="ManifestSha256">The lower-case hex digest of the manifest the signer saw.</param>
public sealed record ManifestSignature(
    string Value,
    string KeyId,
    string RepositoryId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string ManifestSha256);
