namespace EMMA.TestSupport;

/// <summary>An RSA key pair as PEM text.</summary>
/// <param name="PublicPem">The <c>PUBLIC KEY</c> block.</param>
/// <param name="PrivatePem">The <c>PRIVATE KEY</c> block.</param>
public sealed record KeyPair(string PublicPem, string PrivatePem);
