using System.Security.Cryptography;

using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;

namespace EMMA.TestSupport;

/// <summary>
/// A throwaway public-key infrastructure for tests: a repository root key, a delegated signing key,
/// an unrelated attacker key and a weak key, plus builders for signed manifests and delegation files.
/// Keys are generated once per test run.
/// </summary>
public static class TestPki
{
    /// <summary>The repository id used throughout.</summary>
    public const string RepositoryId = "test-repo";

    /// <summary>The trusted root key's id.</summary>
    public const string RootKeyId = "root-1";

    /// <summary>The delegated signer's key id.</summary>
    public const string SignerKeyId = "signer-1";

    /// <summary>Gets the "current time" tests run at.</summary>
    public static DateTimeOffset Now { get; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Gets the repository's root key pair.</summary>
    public static KeyPair Root { get; } = Generate(2048);

    /// <summary>Gets the delegated signing key pair the root key authorizes.</summary>
    public static KeyPair Signer { get; } = Generate(2048);

    /// <summary>Gets an unrelated key pair that nothing authorizes.</summary>
    public static KeyPair Attacker { get; } = Generate(2048);

    /// <summary>Gets a 1024-bit key pair, too weak to accept.</summary>
    public static KeyPair Weak { get; } = Generate(1024);

    /// <summary>Builds a delegated signer entry.</summary>
    /// <param name="scopes">Plugin ids or <c>prefix.*</c> wildcards.</param>
    /// <param name="status">The key's status.</param>
    /// <param name="validFrom">Start of the validity window; defaults to 30 days before <see cref="Now"/>.</param>
    /// <param name="validUntil">End of the validity window; defaults to 30 days after <see cref="Now"/>.</param>
    /// <param name="key">The key; defaults to <see cref="Signer"/>.</param>
    /// <param name="keyId">The key id; defaults to <see cref="SignerKeyId"/>.</param>
    /// <returns>The entry.</returns>
    public static DelegatedSigner Delegate(
        string[]? scopes = null,
        DelegationStatus status = DelegationStatus.Active,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        KeyPair? key = null,
        string keyId = SignerKeyId) => new(
        keyId,
        (key ?? Signer).PublicPem,
        scopes ?? ["com.example.*"],
        validFrom ?? Now.AddDays(-30),
        validUntil ?? Now.AddDays(30),
        status);

    /// <summary>Builds a delegation file signed by the root key.</summary>
    /// <param name="version">The file's version.</param>
    /// <param name="signers">The authorized signers; defaults to one active signer for <c>com.example.*</c>.</param>
    /// <returns>The signed bundle.</returns>
    public static DelegationBundle Bundle(long version = 1, params DelegatedSigner[] signers) =>
        PluginSigner.SignDelegations(
            RepositoryId,
            version,
            Now.AddDays(-1),
            RootKeyId,
            signers.Length == 0 ? [Delegate()] : signers,
            Root.PrivatePem);

    /// <summary>Builds a policy that trusts the test repository's root key.</summary>
    /// <param name="requireSigned">Whether unsigned plugins are refused.</param>
    /// <returns>The policy.</returns>
    public static TrustPolicy Policy(bool requireSigned = true) =>
        TrustPolicy.Create(
            [new TrustedRepository(RepositoryId, new Dictionary<string, string>(StringComparer.Ordinal) { [RootKeyId] = Root.PublicPem })],
            requireSigned);

    /// <summary>Signs a manifest with the delegated signer, valid for a week around <see cref="Now"/>.</summary>
    /// <param name="manifest">The manifest to sign.</param>
    /// <param name="key">The signing key; defaults to <see cref="Signer"/>.</param>
    /// <param name="keyId">The key id; defaults to <see cref="SignerKeyId"/>.</param>
    /// <param name="issuedAt">When signed; defaults to one hour before <see cref="Now"/>.</param>
    /// <param name="expiresAt">When the signature expires; defaults to a week after <see cref="Now"/>.</param>
    /// <returns>The signed manifest.</returns>
    public static PluginManifest Sign(
        PluginManifest manifest,
        KeyPair? key = null,
        string keyId = SignerKeyId,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null) =>
        PluginSigner.SignManifest(
            manifest,
            RepositoryId,
            keyId,
            (key ?? Signer).PrivatePem,
            issuedAt ?? Now.AddHours(-1),
            expiresAt ?? Now.AddDays(7));

    private static KeyPair Generate(int bits)
    {
        using RSA rsa = RSA.Create(bits);
        return new KeyPair(rsa.ExportSubjectPublicKeyInfoPem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}
