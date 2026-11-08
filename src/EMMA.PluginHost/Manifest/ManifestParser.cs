using System.Text.Json;

using EMMA.Domain;

namespace EMMA.PluginHost.Manifest;

/// <summary>
/// Reads a plugin manifest from untrusted JSON. The reader is strict: unknown or repeated
/// properties, wrong types, values outside the schema's limits and unsafe paths are all
/// rejected, naming the first offending field.
/// </summary>
public static class ManifestParser
{
    /// <summary>The largest manifest accepted, in bytes.</summary>
    public const int MaxManifestBytes = 256 * 1024;

    private static readonly JsonDocumentOptions StrictJson = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
    };

    /// <summary>Parses and validates a manifest.</summary>
    /// <param name="json">The UTF-8 JSON text.</param>
    /// <param name="requireSignature">Whether a <c>signature</c> block must be present.</param>
    /// <returns>The manifest, or why it was rejected.</returns>
    public static Outcome<PluginManifest> Parse(ReadOnlyMemory<byte> json, bool requireSignature)
    {
        if (json.Length is 0 or > MaxManifestBytes)
        {
            return Reject($"$: must be between 1 and {MaxManifestBytes} bytes");
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
            PluginManifest? manifest = Read(document.RootElement, requireSignature, errors);
            return errors.First is null && manifest is not null
                ? Outcome.Success(manifest)
                : Reject(errors.First ?? "$: could not be read");
        }
    }

    private static Outcome<PluginManifest> Reject(string message) =>
        Outcome.Failure<PluginManifest>(ErrorCode.InvalidArgument, $"Invalid plugin manifest. {message}.");

    private static PluginManifest? Read(JsonElement root, bool requireSignature, ParseErrors errors)
    {
        JsonFields? fields = JsonFields.Root(root, errors);
        if (fields is null)
        {
            return null;
        }

        fields.RequireInteger("schema", 1, 1);
        PluginId id = ReadId(fields);
        string name = fields.RequireString("name", 80);
        if (string.IsNullOrWhiteSpace(name))
        {
            fields.Reject("name", "must not be blank");
        }

        string version = fields.RequireString("version", 64);
        if (!ManifestRules.IsSemVer(version))
        {
            fields.Reject("version", "must be a semantic version such as 1.2.3");
        }

        string? description = fields.OptionalString("description", 500);
        string? author = fields.OptionalString("author", 120);
        IReadOnlyList<NodeKind> kinds = ReadKinds(fields);
        ManifestIcon? icon = ReadIcon(fields.OptionalObject("icon"));
        ManifestContract contract = ReadContract(fields.RequireObject("contract"));
        string? minHostVersion = ReadRuntime(fields.OptionalObject("runtime"));
        ReadTransport(fields);
        ManifestEntrypoint entrypoint = ReadEntrypoint(fields.RequireObject("entrypoint"));
        ManifestPermissions permissions = ReadPermissions(fields.RequireObject("permissions"));
        ManifestLimits limits = ReadLimits(fields.RequireObject("limits"));
        ManifestSignature? signature = ReadSignature(fields, requireSignature);
        fields.Finish();

        return new PluginManifest(id, name, version, description, author, kinds, icon, contract, minHostVersion, entrypoint, permissions, limits, signature);
    }

    private static PluginId ReadId(JsonFields fields)
    {
        string text = fields.RequireString("id", ContractLimits.MaxPluginIdLength);
        if (PluginId.TryCreate(text, out PluginId id))
        {
            return id;
        }

        fields.Reject("id", "must be a lower-case reverse-DNS id such as com.example.manga");
        return default;
    }

    private static List<NodeKind> ReadKinds(JsonFields fields)
    {
        List<NodeKind> kinds = [];
        foreach (string text in fields.OptionalStringArray("kinds", 64, 128))
        {
            if (!NodeKind.TryCreate(text, out NodeKind kind))
            {
                fields.Reject("kinds", $"'{text}' is not a valid kind");
            }
            else if (kinds.Contains(kind))
            {
                fields.Reject("kinds", $"'{text}' is listed twice");
            }
            else
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }

    private static ManifestIcon? ReadIcon(JsonFields? fields)
    {
        if (fields is null)
        {
            return null;
        }

        double? aspectRatio = fields.OptionalPositiveNumber("aspectRatio");
        string? fit = fields.OptionalString("fit", 16);
        if (fit is not null and not "cover" and not "contain" and not "fill")
        {
            fields.Reject("fit", "must be cover, contain or fill");
        }

        int? width = (int?)fields.OptionalInteger("width", 1, 100_000);
        int? height = (int?)fields.OptionalInteger("height", 1, 100_000);
        fields.Finish();
        return new ManifestIcon(aspectRatio, fit, width, height);
    }

    private static ManifestContract ReadContract(JsonFields? fields)
    {
        if (fields is null)
        {
            return new ManifestContract([], []);
        }

        IReadOnlyList<string> required = ReadInterfaces(fields, "required");
        if (required.Count == 0)
        {
            fields.Reject("required", "must list at least one interface");
        }

        IReadOnlyList<string> optional = ReadInterfaces(fields, "optional");
        fields.Finish();
        return new ManifestContract(required, optional);
    }

    private static IReadOnlyList<string> ReadInterfaces(JsonFields fields, string name)
    {
        IReadOnlyList<string> items = string.Equals(name, "required", StringComparison.Ordinal)
            ? fields.RequireStringArray(name, 64, 128)
            : fields.OptionalStringArray(name, 64, 128);
        foreach (string item in items.Where(item => !ManifestRules.IsInterfaceId(item)))
        {
            fields.Reject(name, $"'{item}' is not an interface id such as emma:core/catalog@1");
        }

        if (items.Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            fields.Reject(name, "lists an interface twice");
        }

        return items;
    }

    private static string? ReadRuntime(JsonFields? fields)
    {
        if (fields is null)
        {
            return null;
        }

        string? minHostVersion = fields.OptionalString("minHostVersion", 32);
        if (minHostVersion is not null && !ManifestRules.IsPlainVersion(minHostVersion))
        {
            fields.Reject("minHostVersion", "must look like 1.2.3");
        }

        fields.Finish();
        return minHostVersion;
    }

    private static void ReadTransport(JsonFields fields)
    {
        string? transport = fields.OptionalString("transport", 16);
        if (transport is not null && !string.Equals(transport, "wasm", StringComparison.Ordinal))
        {
            fields.Reject("transport", "only 'wasm' is supported");
        }
    }

    private static ManifestEntrypoint ReadEntrypoint(JsonFields? fields)
    {
        if (fields is null)
        {
            return new ManifestEntrypoint(string.Empty, string.Empty);
        }

        string path = fields.RequireString("path", ManifestRules.MaxPathLength);
        if (!ManifestRules.IsEntrypointPath(path))
        {
            fields.Reject("path", "must be a safe relative path ending in .wasm");
        }

        string sha256 = fields.RequireString("sha256", 64);
        if (!ManifestRules.IsSha256(sha256))
        {
            fields.Reject("sha256", "must be 64 lower-case hex characters");
        }

        fields.Finish();
        return new ManifestEntrypoint(path, sha256);
    }

    private static ManifestPermissions ReadPermissions(JsonFields? fields)
    {
        if (fields is null)
        {
            return new ManifestPermissions([], false);
        }

        IReadOnlyList<string> network = fields.RequireStringArray("network", 64, 253);
        foreach (string host in network.Where(host => !ManifestRules.IsHostPattern(host)))
        {
            fields.Reject("network", $"'{host}' is not a lower-case host name or *.wildcard");
        }

        if (network.Distinct(StringComparer.Ordinal).Count() != network.Count)
        {
            fields.Reject("network", "lists a host twice");
        }

        bool cache = fields.RequireBoolean("cache");
        fields.Finish();
        return new ManifestPermissions(network, cache);
    }

    private static ManifestLimits ReadLimits(JsonFields? fields)
    {
        if (fields is null)
        {
            return new ManifestLimits(0, 0);
        }

        int cpu = (int)fields.RequireInteger("cpuBudgetMs", 1, 600_000);
        int memory = (int)fields.RequireInteger("memoryMb", 1, 4096);
        fields.Finish();
        return new ManifestLimits(cpu, memory);
    }

    private static ManifestSignature? ReadSignature(JsonFields fields, bool required)
    {
        JsonFields? block = required ? fields.RequireObject("signature") : fields.OptionalObject("signature");
        if (block is null)
        {
            return null;
        }

        string algorithm = block.RequireString("algorithm", 16);
        if (!string.Equals(algorithm, "rsa-sha256", StringComparison.Ordinal))
        {
            block.Reject("algorithm", "must be rsa-sha256");
        }

        string value = block.RequireString("value", 4096);
        if (!FieldReaders.IsBase64(value))
        {
            block.Reject("value", "must be base64");
        }

        string keyId = FieldReaders.Identifier(block, "keyId");
        string repositoryId = FieldReaders.Identifier(block, "repositoryId");
        DateTimeOffset issuedAt = FieldReaders.Timestamp(block, "issuedAt");
        DateTimeOffset expiresAt = FieldReaders.Timestamp(block, "expiresAt");
        if (expiresAt <= issuedAt)
        {
            block.Reject("expiresAt", "must be after issuedAt");
        }

        string digest = block.RequireString("manifestSha256", 64);
        if (!ManifestRules.IsSha256(digest))
        {
            block.Reject("manifestSha256", "must be 64 lower-case hex characters");
        }

        block.Finish();
        return new ManifestSignature(value, keyId, repositoryId, issuedAt, expiresAt, digest);
    }
}
