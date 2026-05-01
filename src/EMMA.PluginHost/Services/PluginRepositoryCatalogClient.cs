using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EMMA.PluginHost.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

public sealed class PluginRepositoryCatalogClient(
    IOptions<PluginHostOptions> options,
    ILogger<PluginRepositoryCatalogClient> logger)
{
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<PluginRepositoryCatalogClient> _logger = logger;

    public async Task<PluginRepositoryCatalogFetchResult> FetchCatalogAsync(
        PluginRepositoryRecord repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var uri = ParseAndValidateRemoteUri(repository.CatalogUrl, allowHttp: _options.AllowInsecureRepositoryHttp);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(repository.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(repository.ETag));
        }

        using var httpClient = CreateHttpClient();
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new PluginRepositoryCatalogFetchResult(
                NotModified: true,
                Catalog: null,
                ETag: repository.ETag,
                RawJson: null,
                RetrievedAtUtc: DateTimeOffset.UtcNow);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                $"Repository '{repository.Id}' returned status {(int)response.StatusCode} while fetching catalog.");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _options.RepositoryMaxCatalogBytes)
        {
            throw new InvalidDataException(
                $"Repository '{repository.Id}' catalog exceeds max allowed size ({_options.RepositoryMaxCatalogBytes} bytes).");
        }

        var rawJson = await ReadContentWithLimitAsync(
            response,
            _options.RepositoryMaxCatalogBytes,
            cancellationToken);

        var catalog = JsonSerializer.Deserialize(
            rawJson,
            PluginRepositoryJsonContext.Default.PluginRepositoryCatalog);

        if (catalog is null)
        {
            throw new InvalidDataException($"Repository '{repository.Id}' catalog response was empty.");
        }

        var eTag = response.Headers.ETag?.Tag;
        return new PluginRepositoryCatalogFetchResult(
            NotModified: false,
            Catalog: catalog,
            ETag: eTag,
            RawJson: rawJson,
            RetrievedAtUtc: DateTimeOffset.UtcNow);
    }

    public async Task<(string FilePath, string Sha256Hex, long ContentLength)> DownloadArtifactAsync(
        string repositoryId,
        string assetUrl,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var uri = ParseAndValidateRemoteUri(assetUrl, allowHttp: _options.AllowInsecureRepositoryHttp);
        Directory.CreateDirectory(destinationDirectory);

        var tempFilePath = Path.Combine(destinationDirectory, $"{repositoryId}-{Guid.NewGuid():N}.zip");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                $"Artifact download failed with status {(int)response.StatusCode}.");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _options.RepositoryMaxArtifactBytes)
        {
            throw new InvalidDataException(
                $"Artifact exceeds max allowed size ({_options.RepositoryMaxArtifactBytes} bytes).");
        }

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

        using var hash = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[64 * 1024];
        long totalRead = 0;

        while (true)
        {
            var read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > _options.RepositoryMaxArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Artifact exceeds max allowed size ({_options.RepositoryMaxArtifactBytes} bytes).");
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.TransformBlock(buffer, 0, read, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);

        var hashBytes = hash.Hash ?? [];
        var sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return (tempFilePath, sha256Hex, totalRead);
    }

    public async Task<string> DownloadTextAsync(
        string url,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var uri = ParseAndValidateRemoteUri(url, allowHttp: _options.AllowInsecureRepositoryHttp);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                $"Request to '{url}' failed with status {(int)response.StatusCode}.");
        }

        return await ReadContentWithLimitAsync(response, maxBytes, cancellationToken);
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };

        var timeoutSeconds = Math.Max(5, _options.RepositoryRequestTimeoutSeconds);
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(_options.RepositoryHttpUserAgent))
        {
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.RepositoryHttpUserAgent.Trim());
            }
            catch (FormatException ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Invalid repository user-agent configured: {UserAgent}", _options.RepositoryHttpUserAgent);
                }
            }
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<string> ReadContentWithLimitAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var memory = new MemoryStream();

        var buffer = new byte[64 * 1024];
        int read;
        var total = 0;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Repository catalog exceeds max allowed size ({maxBytes} bytes).");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static Uri ParseAndValidateRemoteUri(string value, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Remote URL must be an absolute URI.", nameof(value));
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!isHttps && !(allowHttp && isHttp))
        {
            throw new ArgumentException("Remote URL must use https.", nameof(value));
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Remote URL must include a host.", nameof(value));
        }

        return uri;
    }
}
