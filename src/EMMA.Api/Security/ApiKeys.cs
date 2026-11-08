using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EMMA.Api.Security;

/// <summary>
/// Creates, hashes and parses API keys. A key is 256 random bits, so a fast hash is the right tool
/// (there is no weak secret to stretch) and the configuration only ever holds the hash.
/// </summary>
internal static class ApiKeys
{
    /// <summary>The prefix of the hash text in configuration.</summary>
    public const string HashPrefix = "sha256:";

    /// <summary>The longest key worth hashing; longer input is refused before any work.</summary>
    public const int MaxKeyLength = 256;

    private const string KeyPrefix = "emma_";
    private const int KeyBytes = 32;

    /// <summary>Generates a fresh random key and its configuration hash.</summary>
    /// <returns>The key and its hash.</returns>
    public static GeneratedApiKey Generate()
    {
        string key = KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));
        return new GeneratedApiKey(key, FormatHash(Hash(key)));
    }

    /// <summary>Hashes a key.</summary>
    /// <param name="key">The key text.</param>
    /// <returns>The 32-byte SHA-256.</returns>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Formats a hash as configuration text.</summary>
    /// <param name="hash">The 32-byte hash.</param>
    /// <returns>The text, <c>sha256:</c> followed by 64 hex digits.</returns>
    public static string FormatHash(byte[] hash) => HashPrefix + Convert.ToHexStringLower(hash);

    /// <summary>Parses configuration hash text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="hash">The hash when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is <c>sha256:</c> and 64 hex digits.</returns>
    public static bool TryParseHash([NotNullWhen(true)] string? text, [NotNullWhen(true)] out byte[]? hash)
    {
        hash = null;
        if (text is null || !text.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> digits = text.AsSpan(HashPrefix.Length);
        if (digits.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        byte[] parsed = new byte[SHA256.HashSizeInBytes];
        for (int index = 0; index < parsed.Length; index++)
        {
            if (!byte.TryParse(digits.Slice(index * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed[index]))
            {
                return false;
            }
        }

        hash = parsed;
        return true;
    }
}
