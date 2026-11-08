using System.Security.Cryptography;

using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Signing;

/// <summary>
/// Produces signatures: used by repository publishing tools and by tests. It shares its payload
/// builders with <see cref="PluginSignatureVerifier"/>, so what is signed is exactly what is verified.
/// Private keys are passed in by the caller and never stored.
/// </summary>
public static class PluginSigner
{
    /// <summary>Signs a manifest with a delegated key.</summary>
    /// <param name="manifest">The manifest; any existing signature is replaced.</param>
    /// <param name="repositoryId">The repository vouching for the plugin.</param>
    /// <param name="keyId">The delegated key's id.</param>
    /// <param name="privateKeyPem">The delegated key's RSA private key (PEM).</param>
    /// <param name="issuedAt">When the signature is made.</param>
    /// <param name="expiresAt">When the signature stops being valid.</param>
    /// <returns>The manifest with its signature block.</returns>
    public static PluginManifest SignManifest(
        PluginManifest manifest,
        string repositoryId,
        string keyId,
        string privateKeyPem,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        PluginManifest unsigned = manifest with { Signature = null };
        byte[] digest = ManifestDigest.Compute(unsigned);
        byte[] payload = SignaturePayloads.ForManifest(digest, repositoryId, keyId, issuedAt, expiresAt);

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        byte[] signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return unsigned with
        {
            Signature = new ManifestSignature(
                Convert.ToBase64String(signature),
                keyId,
                repositoryId,
                issuedAt,
                expiresAt,
                Convert.ToHexStringLower(digest)),
        };
    }

    /// <summary>Signs a delegation file with a repository root key.</summary>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="version">The file's version; it must only ever increase.</param>
    /// <param name="issuedAt">When the file is signed.</param>
    /// <param name="rootKeyId">The root key's id.</param>
    /// <param name="delegations">The authorized signers.</param>
    /// <param name="rootPrivateKeyPem">The root RSA private key (PEM).</param>
    /// <returns>The signed bundle.</returns>
    /// <exception cref="ArgumentException">A delegated public key is not an acceptable RSA public key.</exception>
    public static DelegationBundle SignDelegations(
        string repositoryId,
        long version,
        DateTimeOffset issuedAt,
        string rootKeyId,
        IReadOnlyList<DelegatedSigner> delegations,
        string rootPrivateKeyPem)
    {
        DelegationBundle unsigned = new(repositoryId, version, issuedAt, rootKeyId, string.Empty, delegations);
        byte[] payload = SignaturePayloads.ForDelegations(unsigned)
            ?? throw new ArgumentException("A delegated public key is not an acceptable RSA public key.", nameof(delegations));

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(rootPrivateKeyPem);
        return unsigned with { Signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    }
}
