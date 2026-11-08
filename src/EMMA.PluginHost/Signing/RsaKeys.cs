using System.Security.Cryptography;

namespace EMMA.PluginHost.Signing;

/// <summary>Reads RSA public keys, accepting only well-formed public keys of a safe size.</summary>
internal static class RsaKeys
{
    /// <summary>The smallest RSA modulus accepted, in bits.</summary>
    public const int MinimumBits = 2048;

    /// <summary>Checks that text is an acceptable public key.</summary>
    /// <param name="pem">The PEM text.</param>
    /// <returns><see langword="true"/> when it is a <c>PUBLIC KEY</c> block holding an RSA key of at least <see cref="MinimumBits"/>.</returns>
    public static bool IsAcceptablePublicKey(string pem) => ReadPublicKeyDer(pem) is not null;

    /// <summary>Reads a public key and returns its canonical DER (SubjectPublicKeyInfo) bytes.</summary>
    /// <param name="pem">The PEM text.</param>
    /// <returns>The DER bytes, or <see langword="null"/> when the text is not an acceptable public key.</returns>
    public static byte[]? ReadPublicKeyDer(string pem) => WithPublicKey(pem, rsa => rsa.ExportSubjectPublicKeyInfo());

    /// <summary>Checks an RSA PKCS#1 v1.5 SHA-256 signature.</summary>
    /// <param name="publicKeyPem">The signer's public key.</param>
    /// <param name="payload">The signed bytes.</param>
    /// <param name="signature">The signature.</param>
    /// <returns><see langword="true"/> only when the key is acceptable and the signature is valid.</returns>
    public static bool Verify(string publicKeyPem, byte[] payload, byte[] signature) =>
        WithPublicKey(publicKeyPem, rsa => rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    /// <summary>
    /// Runs <paramref name="use"/> with the key. A PEM that holds a private key, a certificate
    /// or anything else is refused, as is a key under <see cref="MinimumBits"/>.
    /// </summary>
    private static T? WithPublicKey<T>(string pem, Func<RSA, T> use)
    {
        if (!PemEncoding.TryFind(pem, out PemFields fields) || !pem.AsSpan(fields.Label).SequenceEqual("PUBLIC KEY"))
        {
            return default;
        }

        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pem[fields.Base64Data]), out _);
            return rsa.KeySize >= MinimumBits ? use(rsa) : default;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return default;
        }
    }
}
