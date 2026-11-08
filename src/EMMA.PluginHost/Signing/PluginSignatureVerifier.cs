using System.Security.Cryptography;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Signing;

/// <summary>
/// Decides whether a plugin's signature chain holds: a trusted repository root key signed a
/// delegation file; that file authorizes a delegated key for the plugin's id; that key signed a
/// payload that covers the manifest digest (and through it the entrypoint digest). Every failure
/// is a distinct, plain-language reason and the answer is never "maybe": the caller fails closed.
/// </summary>
/// <remarks>
/// The repository id is taken from the package source, never from the manifest's own claim. A
/// version counter (<see cref="IDelegationVersionPort"/>) stops an old delegation file, validly
/// signed but still listing a since-revoked key as active, from being replayed.
/// </remarks>
/// <param name="policy">Which repositories are trusted.</param>
/// <param name="delegations">Where delegation files come from.</param>
/// <param name="versions">The record of the newest delegation file accepted per repository.</param>
/// <param name="time">The time source.</param>
public sealed class PluginSignatureVerifier(
    TrustPolicy policy,
    IDelegationSource delegations,
    IDelegationVersionPort versions,
    TimeProvider time)
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Verifies a plugin manifest's signature.</summary>
    /// <param name="manifest">The manifest, with its signature block.</param>
    /// <param name="repositoryId">The repository the package came from, as known to the host.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Who vouched for the plugin, or the reason the signature was rejected.</returns>
    public async Task<Outcome<VerifiedSignature>> VerifyAsync(
        PluginManifest manifest,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        ManifestSignature? signature = manifest.Signature;
        if (signature is null)
        {
            return Rejected("the manifest is not signed");
        }

        if (!ManifestRules.IsIdentifier(repositoryId)
            || !string.Equals(signature.RepositoryId, repositoryId, StringComparison.Ordinal))
        {
            return Rejected($"the signature names repository '{signature.RepositoryId}' but the package came from a different one");
        }

        if (!policy.Repositories.TryGetValue(repositoryId, out TrustedRepository? repository))
        {
            return Rejected($"repository '{repositoryId}' is not trusted");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (signature.IssuedAt - AllowedClockSkew > now)
        {
            return Rejected("the signature is dated in the future");
        }

        if (signature.ExpiresAt <= now)
        {
            return Rejected("the signature has expired");
        }

        if (!TryDecode(signature.Value, signature.ManifestSha256, out byte[] signatureBytes, out byte[] claimedDigest))
        {
            return Rejected("the signature or its digest is not valid encoded data");
        }

        byte[] digest = ManifestDigest.Compute(manifest);
        if (!CryptographicOperations.FixedTimeEquals(digest, claimedDigest))
        {
            return Rejected("the manifest was changed after it was signed");
        }

        Outcome<DelegationBundle> bundle = await this.LoadBundleAsync(repository, now, cancellationToken).ConfigureAwait(false);
        if (!bundle.IsSuccess)
        {
            return Outcome.Failure<VerifiedSignature>(bundle.Error);
        }

        Outcome<DelegatedSigner> signer = FindSigner(bundle.Value, signature, manifest.Id);
        if (!signer.IsSuccess)
        {
            return Outcome.Failure<VerifiedSignature>(signer.Error);
        }

        byte[] payload = SignaturePayloads.ForManifest(digest, repositoryId, signature.KeyId, signature.IssuedAt, signature.ExpiresAt);
        return RsaKeys.Verify(signer.Value.PublicKeyPem, payload, signatureBytes)
            ? Outcome.Success(new VerifiedSignature(repositoryId, signature.KeyId, signature.IssuedAt, signature.ExpiresAt))
            : Rejected("the signature does not verify");
    }

    private static Outcome<VerifiedSignature> Rejected(string reason) =>
        Outcome.Failure<VerifiedSignature>(ErrorCode.Unauthorized, $"Plugin signature rejected: {reason}.");

    private static Outcome<DelegationBundle> BundleRejected(string reason) =>
        Outcome.Failure<DelegationBundle>(ErrorCode.Unauthorized, $"Plugin signature rejected: {reason}.");

    private static bool TryDecode(string signatureBase64, string digestHex, out byte[] signature, out byte[] digest)
    {
        signature = [];
        digest = [];

        // The digest is checked as 64 lower-case hex characters before it is decoded, so decoding cannot throw.
        byte[]? decoded = DecodeBase64(signatureBase64);
        if (decoded is null || !ManifestRules.IsSha256(digestHex))
        {
            return false;
        }

        signature = decoded;
        digest = Convert.FromHexString(digestHex);
        return true;
    }

    private static byte[]? DecodeBase64(string text)
    {
        Span<byte> scratch = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text, scratch, out int written) ? scratch[..written].ToArray() : null;
    }

    private static Outcome<DelegatedSigner> FindSigner(DelegationBundle bundle, ManifestSignature signature, PluginId plugin)
    {
        DelegatedSigner? signer = bundle.Delegations.FirstOrDefault(item => string.Equals(item.KeyId, signature.KeyId, StringComparison.Ordinal));
        if (signer is null)
        {
            return SignerRejected($"key '{signature.KeyId}' is not delegated by the repository");
        }

        if (signer.Status == DelegationStatus.Revoked)
        {
            return SignerRejected($"key '{signature.KeyId}' has been revoked");
        }

        // The key must have been authorized when it signed; it need not still be authorized now.
        if (signature.IssuedAt < signer.ValidFrom || signature.IssuedAt > signer.ValidUntil)
        {
            return SignerRejected($"key '{signature.KeyId}' was not valid when the signature was made");
        }

        return signer.Scopes.Any(scope => DelegationScope.Covers(scope, plugin))
            ? Outcome.Success(signer)
            : SignerRejected($"key '{signature.KeyId}' is not authorized for plugin '{plugin}'");
    }

    private static Outcome<DelegatedSigner> SignerRejected(string reason) =>
        Outcome.Failure<DelegatedSigner>(ErrorCode.Unauthorized, $"Plugin signature rejected: {reason}.");

    private async Task<Outcome<DelegationBundle>> LoadBundleAsync(
        TrustedRepository repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? bytes = await delegations.GetAsync(repository.Id, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return BundleRejected($"no delegation file is available for repository '{repository.Id}'");
        }

        Outcome<DelegationBundle> parsed = DelegationJson.Parse(bytes.Value);
        if (!parsed.IsSuccess)
        {
            return BundleRejected("the delegation file is malformed");
        }

        DelegationBundle bundle = parsed.Value;
        if (!string.Equals(bundle.RepositoryId, repository.Id, StringComparison.Ordinal))
        {
            return BundleRejected("the delegation file belongs to a different repository");
        }

        if (!repository.RootKeys.TryGetValue(bundle.RootKeyId, out string? rootKey))
        {
            return BundleRejected($"root key '{bundle.RootKeyId}' is not trusted");
        }

        byte[]? payload = SignaturePayloads.ForDelegations(bundle);
        byte[]? rootSignature = DecodeBase64(bundle.Signature);
        if (payload is null || rootSignature is null || !RsaKeys.Verify(rootKey, payload, rootSignature))
        {
            return BundleRejected("the delegation file's root signature does not verify");
        }

        if (bundle.IssuedAt - AllowedClockSkew > now)
        {
            return BundleRejected("the delegation file is dated in the future");
        }

        // Only an authentic file may move the counter, so unsigned junk cannot lock real updates out.
        return await versions.TryAdvanceAsync(repository.Id, bundle.Version, cancellationToken).ConfigureAwait(false)
            ? Outcome.Success(bundle)
            : BundleRejected("the delegation file is older than one already accepted");
    }
}
