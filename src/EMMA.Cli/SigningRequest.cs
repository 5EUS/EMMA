namespace EMMA.Cli;

/// <summary>How to sign a package.</summary>
/// <param name="Repository">The repository id the signature is made for.</param>
/// <param name="KeyId">The id of the signing key in the repository's delegation file.</param>
/// <param name="PrivateKeyPem">The private key, PEM.</param>
/// <param name="ValidDays">How many days the signature stays valid.</param>
internal sealed record SigningRequest(string Repository, string KeyId, string PrivateKeyPem, int ValidDays);
