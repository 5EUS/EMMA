using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace EMMA.PluginHost.Manifest;

/// <summary>Writes a manifest as JSON, in the shape <see cref="ManifestParser"/> reads. Used by packaging and signing tools.</summary>
public static class ManifestWriter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Serializes a manifest, including its signature when it has one.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The UTF-8 JSON text.</returns>
    public static byte[] Write(PluginManifest manifest)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", 1);
            json.WriteString("id", manifest.Id.Value);
            json.WriteString("name", manifest.Name);
            json.WriteString("version", manifest.Version);
            WriteOptionalString(json, "description", manifest.Description);
            WriteOptionalString(json, "author", manifest.Author);
            WriteStrings(json, "kinds", [.. manifest.Kinds.Select(kind => kind.Value)]);
            WriteIcon(json, manifest.Icon);
            WriteContract(json, manifest.Contract);
            WriteRuntime(json, manifest.MinHostVersion);
            json.WriteString("transport", "wasm");
            json.WriteStartObject("entrypoint");
            json.WriteString("path", manifest.Entrypoint.Path);
            json.WriteString("sha256", manifest.Entrypoint.Sha256);
            json.WriteEndObject();
            json.WriteStartObject("permissions");
            WriteStrings(json, "network", manifest.Permissions.Network);
            if (manifest.Permissions.NetworkSettings.Count > 0)
            {
                WriteStrings(json, "networkSettings", manifest.Permissions.NetworkSettings);
            }

            json.WriteBoolean("cache", manifest.Permissions.Cache);
            json.WriteEndObject();
            json.WriteStartObject("limits");
            json.WriteNumber("cpuBudgetMs", manifest.Limits.CpuBudgetMs);
            json.WriteNumber("memoryMb", manifest.Limits.MemoryMb);
            json.WriteEndObject();
            WriteSignature(json, manifest.Signature);
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Formats a timestamp the way manifests and delegation files store it.</summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>UTC with millisecond precision, such as <c>2026-01-01T00:00:00.000Z</c>.</returns>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void WriteOptionalString(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteIcon(Utf8JsonWriter json, ManifestIcon? icon)
    {
        if (icon is null)
        {
            return;
        }

        json.WriteStartObject("icon");
        if (icon.AspectRatio is { } ratio)
        {
            json.WriteNumber("aspectRatio", ratio);
        }

        WriteOptionalString(json, "fit", icon.Fit);
        if (icon.Width is { } width)
        {
            json.WriteNumber("width", width);
        }

        if (icon.Height is { } height)
        {
            json.WriteNumber("height", height);
        }

        json.WriteEndObject();
    }

    private static void WriteContract(Utf8JsonWriter json, ManifestContract contract)
    {
        json.WriteStartObject("contract");
        WriteStrings(json, "required", contract.Required);
        WriteStrings(json, "optional", contract.Optional);
        json.WriteEndObject();
    }

    private static void WriteRuntime(Utf8JsonWriter json, string? minHostVersion)
    {
        if (minHostVersion is null)
        {
            return;
        }

        json.WriteStartObject("runtime");
        json.WriteString("minHostVersion", minHostVersion);
        json.WriteEndObject();
    }

    private static void WriteSignature(Utf8JsonWriter json, ManifestSignature? signature)
    {
        if (signature is null)
        {
            return;
        }

        json.WriteStartObject("signature");
        json.WriteString("algorithm", "rsa-sha256");
        json.WriteString("value", signature.Value);
        json.WriteString("keyId", signature.KeyId);
        json.WriteString("repositoryId", signature.RepositoryId);
        json.WriteString("issuedAt", FormatTimestamp(signature.IssuedAt));
        json.WriteString("expiresAt", FormatTimestamp(signature.ExpiresAt));
        json.WriteString("manifestSha256", signature.ManifestSha256);
        json.WriteEndObject();
    }
}
