using System.Security.Cryptography;

using EMMA.Api.Configuration;

namespace EMMA.Api.Security;

/// <summary>
/// Checks a presented key against the configured hashes. The comparison does not stop at the first
/// match and does not branch on the secret, so timing says nothing about which key is close.
/// </summary>
/// <param name="keys">The configured keys.</param>
internal sealed class ApiKeyValidator(IReadOnlyList<ApiClientKey> keys)
{
    /// <summary>Identifies the client a key belongs to.</summary>
    /// <param name="presented">The key from the request.</param>
    /// <returns>The client, or <see langword="null"/> when the key is missing, oversized or unknown.</returns>
    public ApiClient? Validate(string? presented)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > ApiKeys.MaxKeyLength)
        {
            return null;
        }

        byte[] hash = ApiKeys.Hash(presented);
        ApiClient? match = null;
        foreach (ApiClientKey key in keys.Where(candidate => CryptographicOperations.FixedTimeEquals(hash, candidate.KeyHash)))
        {
            match = new ApiClient(key.ClientId);
        }

        return match;
    }
}
