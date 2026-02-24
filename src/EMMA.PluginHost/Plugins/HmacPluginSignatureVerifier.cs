using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

public sealed class HmacPluginSignatureVerifier(IOptions<PluginSignatureOptions> options) : IPluginSignatureVerifier
{
    private readonly PluginSignatureOptions _options = options.Value;

    public bool Verify(PluginManifest manifest, out string? reason)
    {
        reason = null;

        var signature = manifest.Signature;
        if (signature is null)
        {
            reason = "Plugin manifest signature is missing.";
            return false;
        }

        if (!string.Equals(signature.Algorithm, "hmac-sha256", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Unsupported signature algorithm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.HmacKeyBase64))
        {
            reason = "Plugin signature key not configured.";
            return false;
        }

        if (!TryDecodeKey(_options.HmacKeyBase64, out var key))
        {
            reason = "Invalid plugin signature key.";
            return false;
        }

        var payload = BuildPayload(manifest);
        var computed = ComputeHmac(payload, key);

        if (!CryptographicOperations.FixedTimeEquals(computed, DecodeSignature(signature.Value)))
        {
            reason = "Plugin signature mismatch.";
            return false;
        }

        return true;
    }

    private static string BuildPayload(PluginManifest manifest)
    {
        var entry = manifest.Entry;
        var args = entry?.Arguments is null ? string.Empty : string.Join(" ", entry.Arguments);
        var payload = string.Join("|",
            manifest.Id ?? string.Empty,
            manifest.Version ?? string.Empty,
            entry?.Protocol ?? string.Empty,
            entry?.Endpoint ?? string.Empty,
            entry?.Executable ?? string.Empty,
            entry?.Startup ?? string.Empty,
            entry?.WorkingDirectory ?? string.Empty,
            args);

        return payload;
    }

    private static bool TryDecodeKey(string base64, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(base64);
            return true;
        }
        catch
        {
            key = Array.Empty<byte>();
            return false;
        }
    }

    private static byte[] ComputeHmac(string payload, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static byte[] DecodeSignature(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<byte>();
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
