using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Storage;

/// <summary>
/// File-backed cache for page assets using storage temp asset roots.
/// </summary>
public sealed class PageAssetStorageCache(StorageOptions options) : IPageAssetCachePort
{
    private readonly StorageOptions _options = options;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
#pragma warning disable SYSLIB0049
        options.AddContext<StorageJsonContext>();
#pragma warning restore SYSLIB0049
        return options;
    }

    public async Task<MediaPageAsset?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var basePath = GetBasePath(key);
        var payloadPath = basePath + ".bin";
        var metadataPath = basePath + ".json";

        if (!File.Exists(payloadPath) || !File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<PageAssetMetadata>(metadataJson, JsonOptions);
            if (metadata is null)
            {
                return null;
            }

            var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken);
            var contentType = string.IsNullOrWhiteSpace(metadata.ContentType)
                ? "application/octet-stream"
                : metadata.ContentType;

            return new MediaPageAsset(contentType, payload, metadata.FetchedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetAsync(string key, MediaPageAsset asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var basePath = GetBasePath(key);
        var directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payloadPath = basePath + ".bin";
        var metadataPath = basePath + ".json";
        var payload = asset.Payload ?? Array.Empty<byte>();
        var metadata = new PageAssetMetadata(
            string.IsNullOrWhiteSpace(asset.ContentType) ? "application/octet-stream" : asset.ContentType,
            asset.FetchedAtUtc);

        try
        {
            await File.WriteAllBytesAsync(payloadPath, payload, cancellationToken);
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
            await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
        }
        catch
        {
        }
    }

    private string GetBasePath(string key)
    {
        var hash = ComputeHash(key ?? string.Empty);
        return StoragePaths.GetTempAssetPath(_options.TempAssetRootDirectory, hash);
    }

    private static string ComputeHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public sealed record PageAssetMetadata(string ContentType, DateTimeOffset FetchedAtUtc);
}
