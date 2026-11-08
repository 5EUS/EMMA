using System.Buffers;
using System.Text.Json;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Signing;

/// <summary>Reads and writes a repository's delegation file. Reading is as strict as for manifests.</summary>
public static class DelegationJson
{
    /// <summary>The largest delegation file accepted, in bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    private static readonly JsonDocumentOptions StrictJson = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
    };

    /// <summary>Parses and validates a delegation file. The root signature is checked later, by the verifier.</summary>
    /// <param name="json">The UTF-8 JSON text.</param>
    /// <returns>The bundle, or why it was rejected.</returns>
    public static Outcome<DelegationBundle> Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > MaxBytes)
        {
            return Reject($"$: must be between 1 and {MaxBytes} bytes");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, StrictJson);
        }
        catch (JsonException ex)
        {
            return Reject($"$: not valid JSON ({ex.Message})");
        }

        using (document)
        {
            ParseErrors errors = new();
            DelegationBundle? bundle = Read(document.RootElement, errors);
            return errors.First is null && bundle is not null
                ? Outcome.Success(bundle)
                : Reject(errors.First ?? "$: could not be read");
        }
    }

    /// <summary>Serializes a bundle. Used by signing tools.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>The UTF-8 JSON text.</returns>
    public static byte[] Write(DelegationBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("repositoryId", bundle.RepositoryId);
            json.WriteNumber("version", bundle.Version);
            json.WriteString("issuedAt", ManifestWriter.FormatTimestamp(bundle.IssuedAt));
            json.WriteString("rootKeyId", bundle.RootKeyId);
            json.WriteStartObject("signature");
            json.WriteString("algorithm", "rsa-sha256");
            json.WriteString("value", bundle.Signature);
            json.WriteEndObject();
            json.WriteStartArray("delegations");
            foreach (DelegatedSigner signer in bundle.Delegations)
            {
                json.WriteStartObject();
                json.WriteString("keyId", signer.KeyId);
                json.WriteString("publicKeyPem", signer.PublicKeyPem);
                json.WriteStartArray("scopes");
                foreach (string scope in signer.Scopes)
                {
                    json.WriteStringValue(scope);
                }

                json.WriteEndArray();
                json.WriteString("validFrom", ManifestWriter.FormatTimestamp(signer.ValidFrom));
                json.WriteString("validUntil", ManifestWriter.FormatTimestamp(signer.ValidUntil));
                json.WriteString("status", signer.Status.ToString().ToUpperInvariant());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static Outcome<DelegationBundle> Reject(string message) =>
        Outcome.Failure<DelegationBundle>(ErrorCode.InvalidArgument, $"Invalid delegation file. {message}.");

    private static DelegationBundle? Read(JsonElement root, ParseErrors errors)
    {
        JsonFields? fields = JsonFields.Root(root, errors);
        if (fields is null)
        {
            return null;
        }

        string repositoryId = FieldReaders.Identifier(fields, "repositoryId");
        long version = fields.RequireInteger("version", 0, 9_007_199_254_740_991);
        DateTimeOffset issuedAt = FieldReaders.Timestamp(fields, "issuedAt");
        string rootKeyId = FieldReaders.Identifier(fields, "rootKeyId");
        string signature = ReadSignature(fields.RequireObject("signature"));
        IReadOnlyList<DelegatedSigner> delegations = fields.RequireObjectArray("delegations", 256, ReadSigner);
        if (delegations.Select(signer => signer.KeyId).Distinct(StringComparer.Ordinal).Count() != delegations.Count)
        {
            fields.Reject("delegations", "lists a key id twice");
        }

        fields.Finish();
        return new DelegationBundle(repositoryId, version, issuedAt, rootKeyId, signature, delegations);
    }

    private static string ReadSignature(JsonFields? block)
    {
        if (block is null)
        {
            return string.Empty;
        }

        if (!string.Equals(block.RequireString("algorithm", 16), "rsa-sha256", StringComparison.Ordinal))
        {
            block.Reject("algorithm", "must be rsa-sha256");
        }

        string value = block.RequireString("value", 4096);
        if (!FieldReaders.IsBase64(value))
        {
            block.Reject("value", "must be base64");
        }

        block.Finish();
        return value;
    }

    private static DelegatedSigner ReadSigner(JsonFields fields)
    {
        string keyId = FieldReaders.Identifier(fields, "keyId");
        string pem = fields.RequireString("publicKeyPem", 4096);
        if (!RsaKeys.IsAcceptablePublicKey(pem))
        {
            fields.Reject("publicKeyPem", $"must be a PUBLIC KEY block holding an RSA key of at least {RsaKeys.MinimumBits} bits");
        }

        IReadOnlyList<string> scopes = fields.RequireStringArray("scopes", 64, 128);
        if (scopes.Count == 0 || !scopes.All(DelegationScope.IsValid))
        {
            fields.Reject("scopes", "must list plugin ids or prefix.* wildcards, and at least one");
        }

        DateTimeOffset validFrom = FieldReaders.Timestamp(fields, "validFrom");
        DateTimeOffset validUntil = FieldReaders.Timestamp(fields, "validUntil");
        if (validUntil <= validFrom)
        {
            fields.Reject("validUntil", "must be after validFrom");
        }

        DelegationStatus status = ReadStatus(fields);
        fields.Finish();
        return new DelegatedSigner(keyId, pem, scopes, validFrom, validUntil, status);
    }

    private static DelegationStatus ReadStatus(JsonFields fields)
    {
        string text = fields.RequireString("status", 16);
        if (Enum.TryParse(text, ignoreCase: true, out DelegationStatus status) && Enum.IsDefined(status))
        {
            return status;
        }

        fields.Reject("status", "must be ACTIVE, RETIRED or REVOKED");
        return DelegationStatus.Revoked;
    }
}
