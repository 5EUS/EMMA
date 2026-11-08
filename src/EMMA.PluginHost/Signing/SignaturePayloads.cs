namespace EMMA.PluginHost.Signing;

/// <summary>
/// The exact bytes each kind of signature covers. Signers and verifiers both call these, so
/// they cannot disagree, and each starts with its own domain tag so a signature made for one
/// purpose can never be replayed as another.
/// </summary>
internal static class SignaturePayloads
{
    private const string ManifestDomain = "EMMA-PLUGIN-SIGNATURE/1";
    private const string DelegationDomain = "EMMA-DELEGATION/1";

    /// <summary>Builds the bytes a delegated key signs for a plugin.</summary>
    /// <param name="manifestDigest">The raw manifest digest; it already covers the entrypoint digest.</param>
    /// <param name="repositoryId">The repository vouching for the plugin.</param>
    /// <param name="keyId">The signing key.</param>
    /// <param name="issuedAt">When it was signed.</param>
    /// <param name="expiresAt">When the signature stops being valid.</param>
    /// <returns>The payload.</returns>
    public static byte[] ForManifest(
        ReadOnlySpan<byte> manifestDigest,
        string repositoryId,
        string keyId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt) =>
        new FrameWriter()
            .Text(ManifestDomain)
            .Bytes(manifestDigest)
            .Text(repositoryId)
            .Text(keyId)
            .Integer(issuedAt.ToUnixTimeMilliseconds())
            .Integer(expiresAt.ToUnixTimeMilliseconds())
            .ToArray();

    /// <summary>
    /// Builds the bytes a repository root key signs for a delegation file. Signers are sorted by
    /// key id, keys enter as canonical DER (so PEM whitespace is irrelevant), and scopes are sorted.
    /// </summary>
    /// <param name="bundle">The bundle; its own signature is not part of the payload.</param>
    /// <returns>The payload, or <see langword="null"/> when a delegated public key is not acceptable.</returns>
    public static byte[]? ForDelegations(DelegationBundle bundle)
    {
        FrameWriter frame = new FrameWriter()
            .Text(DelegationDomain)
            .Text(bundle.RepositoryId)
            .Integer(bundle.Version)
            .Integer(bundle.IssuedAt.ToUnixTimeMilliseconds())
            .Text(bundle.RootKeyId)
            .Integer(bundle.Delegations.Count);

        foreach (DelegatedSigner signer in bundle.Delegations.OrderBy(item => item.KeyId, StringComparer.Ordinal))
        {
            byte[]? der = RsaKeys.ReadPublicKeyDer(signer.PublicKeyPem);
            if (der is null)
            {
                return null;
            }

            frame
                .Text(signer.KeyId)
                .Text(signer.Status.ToString())
                .Integer(signer.ValidFrom.ToUnixTimeMilliseconds())
                .Integer(signer.ValidUntil.ToUnixTimeMilliseconds())
                .Bytes(der)
                .TextList([.. signer.Scopes.Order(StringComparer.Ordinal)]);
        }

        return frame.ToArray();
    }
}
