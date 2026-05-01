using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Plugins;

public sealed class DelegatedPluginSignatureVerifier(
    IOptions<PluginSignatureOptions> options,
    IOptions<PluginHostOptions> hostOptions,
    ILogger<DelegatedPluginSignatureVerifier> logger) : IPluginSignatureVerifier
{
    private const string SignatureAlgorithm = "rsa-sha256";

    private readonly PluginSignatureOptions _options = options.Value;
    private readonly PluginHostOptions _hostOptions = hostOptions.Value;
    private readonly ILogger<DelegatedPluginSignatureVerifier> _logger = logger;

    public bool Verify(PluginManifest manifest, out string? reason)
    {
        reason = null;

        var signature = manifest.Signature;
        if (signature is null)
        {
            reason = "Plugin manifest signature is missing.";
            return false;
        }

        if (!string.Equals(signature.Algorithm, SignatureAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Unsupported signature algorithm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature.Value)
            || string.IsNullOrWhiteSpace(signature.KeyId)
            || string.IsNullOrWhiteSpace(signature.RepositoryId)
            || string.IsNullOrWhiteSpace(signature.IssuedAtUtc)
            || string.IsNullOrWhiteSpace(signature.ManifestDigestSha256))
        {
            reason = "Signature metadata is incomplete.";
            return false;
        }

        if (!TryParseUtc(signature.IssuedAtUtc, out var issuedAtUtc))
        {
            reason = "Signature issuedAtUtc is invalid.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(signature.ExpiresAtUtc)
            && !TryParseUtc(signature.ExpiresAtUtc, out var expiresAtUtc))
        {
            reason = "Signature expiresAtUtc is invalid.";
            return false;
        }

        if (DateTimeOffset.UtcNow < issuedAtUtc)
        {
            reason = "Signature is not valid yet.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(signature.ExpiresAtUtc)
            && TryParseUtc(signature.ExpiresAtUtc, out var parsedExpiry)
            && DateTimeOffset.UtcNow > parsedExpiry)
        {
            reason = "Signature has expired.";
            return false;
        }

        if (!TryDecodeBase64(signature.Value, out var signatureBytes))
        {
            reason = "Signature value is not valid base64.";
            return false;
        }

        if (!TryLoadDelegationBundle(signature.RepositoryId!, out var bundle, out reason))
        {
            return false;
        }

        if (!TryFindDelegatedSigner(bundle, signature.KeyId!, manifest.Id, out var delegatedKey, out reason))
        {
            return false;
        }

        var payload = BuildSignaturePayload(
            manifest,
            signature.RepositoryId!,
            signature.ManifestDigestSha256,
            signature.PayloadDigestSha256,
            signature.IssuedAtUtc,
            signature.ExpiresAtUtc);

        if (!VerifyRsaSignature(delegatedKey.PublicKeyPem, payload, signatureBytes))
        {
            reason = "Plugin signature mismatch.";
            return false;
        }

        return true;
    }

    private bool TryLoadDelegationBundle(string repositoryId, out DelegationBundle bundle, out string? reason)
    {
        reason = null;
        bundle = default!;

        var delegationDirectory = ResolveDelegationDirectory();
        var rootKeyDirectory = ResolveRootKeyDirectory();

        if (string.IsNullOrWhiteSpace(delegationDirectory)
            || string.IsNullOrWhiteSpace(rootKeyDirectory))
        {
            reason = "Plugin signature trust directories are not configured.";
            return false;
        }

        var delegationPath = Path.Combine(delegationDirectory, $"{repositoryId}.delegations.json");
        if (!File.Exists(delegationPath))
        {
            reason = $"Delegation metadata not found for repository '{repositoryId}'.";
            return false;
        }

        DelegationBundle parsed;
        try
        {
            parsed = ParseDelegationBundle(delegationPath);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to parse delegation metadata: {Path}", delegationPath);
            }

            reason = "Delegation metadata is invalid.";
            return false;
        }

        if (!string.Equals(parsed.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Delegation repositoryId does not match signature repository context.";
            return false;
        }

        var rootKeyPath = Path.Combine(rootKeyDirectory, $"{parsed.RootKeyId}.pem");
        if (!File.Exists(rootKeyPath))
        {
            reason = $"Trusted root key is missing for keyId '{parsed.RootKeyId}'.";
            return false;
        }

        var rootSignaturePayload = BuildDelegationRootPayload(parsed);
        if (!TryDecodeBase64(parsed.RootSignatureValue, out var rootSignatureBytes))
        {
            reason = "Delegation root signature is not valid base64.";
            return false;
        }

        var rootPem = File.ReadAllText(rootKeyPath);
        if (!VerifyRsaSignature(rootPem, rootSignaturePayload, rootSignatureBytes))
        {
            reason = "Delegation root signature verification failed.";
            return false;
        }

        bundle = parsed;
        return true;
    }

    private string? ResolveDelegationDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.DelegationDirectory) && Directory.Exists(_options.DelegationDirectory))
        {
            return _options.DelegationDirectory;
        }

        foreach (var candidate in EnumerateDelegationDirectoryCandidates())
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? ResolveRootKeyDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.RootKeyDirectory) && Directory.Exists(_options.RootKeyDirectory))
        {
            return _options.RootKeyDirectory;
        }

        foreach (var candidate in EnumerateRootKeyDirectoryCandidates())
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateDelegationDirectoryCandidates()
    {
        var manifestDirectory = _hostOptions.ManifestDirectory;
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            yield return Path.Combine(manifestDirectory, "trust");
            yield return Path.Combine(manifestDirectory, "delegations");

            var parent = Path.GetDirectoryName(manifestDirectory);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                yield return Path.Combine(parent, "trust");
                yield return Path.Combine(parent, "delegations");
            }
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, "manifests", "trust");
            yield return Path.Combine(baseDirectory, "manifests", "delegations");
            yield return Path.Combine(baseDirectory, "trust");
            yield return Path.Combine(baseDirectory, "delegations");
        }
    }

    private IEnumerable<string> EnumerateRootKeyDirectoryCandidates()
    {
        var manifestDirectory = _hostOptions.ManifestDirectory;
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            yield return Path.Combine(manifestDirectory, "trust", "roots");

            var parent = Path.GetDirectoryName(manifestDirectory);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                yield return Path.Combine(parent, "trust", "roots");
            }
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, "manifests", "trust", "roots");
            yield return Path.Combine(baseDirectory, "trust", "roots");
            yield return Path.Combine(baseDirectory, "roots");
        }
    }

    private static bool TryFindDelegatedSigner(
        DelegationBundle bundle,
        string keyId,
        string pluginId,
        out DelegatedSigner signer,
        out string? reason)
    {
        signer = default!;
        reason = null;

        var match = bundle.Delegations
            .FirstOrDefault(item => string.Equals(item.KeyId, keyId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            reason = "Signer keyId is not delegated for repository.";
            return false;
        }

        if (!string.Equals(match.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Signer key is not active.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < match.ValidFromUtc || now > match.ValidUntilUtc)
        {
            reason = "Signer key is outside its valid time window.";
            return false;
        }

        if (!IsPluginInScope(pluginId, match.Scopes))
        {
            reason = "Signer key scope does not authorize this plugin id.";
            return false;
        }

        signer = match;
        return true;
    }

    private static bool IsPluginInScope(string pluginId, IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0)
        {
            return false;
        }

        foreach (var scope in scopes)
        {
            var trimmed = scope.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.EndsWith('*'))
            {
                var prefix = trimmed[..^1];
                if (pluginId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(pluginId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeManifestDigest(PluginManifest manifest)
    {
        var manifestWithoutSignature = manifest with { Signature = null };
        var serialized = JsonSerializer.Serialize(manifestWithoutSignature, PluginManifestJsonContext.Default.PluginManifest);
        using var doc = JsonDocument.Parse(serialized);

        var canonical = CanonicalizeJson(doc.RootElement);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static byte[] BuildSignaturePayload(
        PluginManifest manifest,
        string repositoryId,
        string manifestDigestSha256,
        string? payloadDigestSha256,
        string? issuedAtUtc,
        string? expiresAtUtc)
    {
        var payload = string.Join("\n", [
            $"pluginId={manifest.Id ?? string.Empty}",
            $"version={manifest.Version ?? string.Empty}",
            $"protocol={manifest.Protocol ?? string.Empty}",
            $"repositoryId={repositoryId}",
            $"manifestDigestSha256={manifestDigestSha256}",
            $"payloadDigestSha256={payloadDigestSha256 ?? string.Empty}",
            $"issuedAtUtc={issuedAtUtc ?? string.Empty}",
            $"expiresAtUtc={expiresAtUtc ?? string.Empty}"
        ]);

        return Encoding.UTF8.GetBytes(payload);
    }

    private static bool VerifyRsaSignature(string publicKeyPem, byte[] payload, byte[] signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    private static bool TryParseUtc(string value, out DateTimeOffset utc)
    {
        if (!DateTimeOffset.TryParse(value, out var parsed))
        {
            utc = default;
            return false;
        }

        utc = parsed.ToUniversalTime();
        return true;
    }

    private static DelegationBundle ParseDelegationBundle(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var repositoryId = GetRequiredString(root, "repositoryId");
        var rootKeyId = GetRequiredString(root, "rootKeyId");
        var issuedAtUtcText = GetRequiredString(root, "issuedAtUtc");
        var version = root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.Number
            ? versionElement.GetInt32()
            : 1;

        if (!TryParseUtc(issuedAtUtcText, out var issuedAtUtc))
        {
            throw new InvalidDataException("Delegation issuedAtUtc is invalid.");
        }

        if (!root.TryGetProperty("signature", out var signatureElement) || signatureElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Delegation signature block is missing.");
        }

        var signatureAlgorithm = GetRequiredString(signatureElement, "algorithm");
        var signatureValue = GetRequiredString(signatureElement, "value");

        if (!string.Equals(signatureAlgorithm, SignatureAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Delegation signature algorithm must be rsa-sha256.");
        }

        if (!root.TryGetProperty("delegations", out var delegationsElement) || delegationsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Delegation list is missing.");
        }

        var delegations = new List<DelegatedSigner>();
        foreach (var element in delegationsElement.EnumerateArray())
        {
            var keyId = GetRequiredString(element, "keyId");
            var publicKeyPem = GetRequiredString(element, "publicKeyPem");
            var status = GetRequiredString(element, "status");
            var validFromUtcText = GetRequiredString(element, "validFromUtc");
            var validUntilUtcText = GetRequiredString(element, "validUntilUtc");

            if (!TryParseUtc(validFromUtcText, out var validFromUtc)
                || !TryParseUtc(validUntilUtcText, out var validUntilUtc))
            {
                throw new InvalidDataException("Delegation key validity range is invalid.");
            }

            var scopes = new List<string>();
            if (element.TryGetProperty("scopes", out var scopesElement)
                && scopesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var scope in scopesElement.EnumerateArray())
                {
                    if (scope.ValueKind == JsonValueKind.String)
                    {
                        var value = scope.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            scopes.Add(value.Trim());
                        }
                    }
                }
            }

            delegations.Add(new DelegatedSigner(
                keyId,
                publicKeyPem,
                scopes,
                validFromUtcText,
                validUntilUtcText,
                validFromUtc,
                validUntilUtc,
                status));
        }

        return new DelegationBundle(
            RepositoryId: repositoryId,
            Version: version,
            IssuedAtUtcText: issuedAtUtcText,
            IssuedAtUtc: issuedAtUtc,
            RootKeyId: rootKeyId,
            RootSignatureValue: signatureValue,
            Delegations: delegations);
    }

    private static byte[] BuildDelegationRootPayload(DelegationBundle bundle)
    {
        var lines = new List<string>
        {
            $"repositoryId={bundle.RepositoryId}",
            $"version={bundle.Version}",
            $"issuedAtUtc={bundle.IssuedAtUtcText}"
        };

        var ordered = bundle.Delegations
            .OrderBy(item => item.KeyId, StringComparer.Ordinal)
            .ToList();

        foreach (var delegatedSigner in ordered)
        {
            lines.Add($"delegation.keyId={delegatedSigner.KeyId}");
            lines.Add($"delegation.status={delegatedSigner.Status}");
            lines.Add($"delegation.validFromUtc={delegatedSigner.ValidFromUtcText}");
            lines.Add($"delegation.validUntilUtc={delegatedSigner.ValidUntilUtcText}");
            lines.Add($"delegation.publicKeyPem={delegatedSigner.PublicKeyPem.Trim()}");

            foreach (var scope in delegatedSigner.Scopes.OrderBy(item => item, StringComparer.Ordinal))
            {
                lines.Add($"delegation.scope={scope}");
            }
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonicalJson(element, writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Missing required string property '{propertyName}'.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Property '{propertyName}' must not be empty.");
        }

        return value.Trim();
    }

    private sealed record DelegationBundle(
        string RepositoryId,
        int Version,
        string IssuedAtUtcText,
        DateTimeOffset IssuedAtUtc,
        string RootKeyId,
        string RootSignatureValue,
        IReadOnlyList<DelegatedSigner> Delegations);

    private sealed record DelegatedSigner(
        string KeyId,
        string PublicKeyPem,
        IReadOnlyList<string> Scopes,
        string ValidFromUtcText,
        string ValidUntilUtcText,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset ValidUntilUtc,
        string Status);
}
