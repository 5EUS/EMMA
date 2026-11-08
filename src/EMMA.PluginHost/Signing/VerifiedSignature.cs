namespace EMMA.PluginHost.Signing;

/// <summary>The result of a successful signature check: who vouched for the plugin.</summary>
/// <param name="RepositoryId">The repository that vouches for it.</param>
/// <param name="KeyId">The delegated key that signed it.</param>
/// <param name="IssuedAt">When it was signed.</param>
/// <param name="ExpiresAt">When the signature stops being valid.</param>
public sealed record VerifiedSignature(string RepositoryId, string KeyId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>Gets the signer as recorded in the registry: <c>repositoryId/keyId</c>.</summary>
    public string Signer => $"{this.RepositoryId}/{this.KeyId}";
}
