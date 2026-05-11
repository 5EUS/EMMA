using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Compression;

/// <summary>
/// Derives Brotli-compressed artifacts for paged media source assets.
/// </summary>
public sealed class PagedMediaCompressionAdapter(IPageAssetFetcherPort pageAssetFetcher) : ICompressionAdapterPort
{
    private readonly IPageAssetFetcherPort _pageAssetFetcher = pageAssetFetcher;

    public IReadOnlyList<MediaType> SupportedMediaTypes { get; } = [MediaType.Paged];

    public Task<CompressionPlan> PlanAsync(CompressionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePagedRequest(request);

        CompressionPlanEntry[] outputs =
        [
            new(
                request.Profile,
                request.PreferredContentType?.Trim() ?? "application/octet-stream",
                ContentEncoding: "br",
                IsRequired: true,
                Metadata: new Dictionary<string, string>
                {
                    ["strategy"] = "brotli-page-asset"
                })
        ];

        return Task.FromResult(new CompressionPlan(
            request,
            outputs,
            RequiresBackgroundGeneration: false,
            AllowSourceFallback: true,
            Reason: "Compress paged source assets with Brotli for derived storage and transport."));
    }

    public async Task<IReadOnlyList<CompressionArtifact>> GenerateAsync(
        CompressionRequest request,
        CancellationToken cancellationToken)
    {
        EnsurePagedRequest(request);

        if (!Uri.TryCreate(request.SourceKey, UriKind.Absolute, out var sourceUri))
        {
            throw new InvalidOperationException("Paged compression requests require SourceKey to be an absolute URI.");
        }

        var asset = await _pageAssetFetcher.FetchAsync(sourceUri, cancellationToken);
        var payload = Compress(asset.Payload);
        var artifact = new CompressionArtifact(
            ArtifactId: CreateArtifactId(request),
            MediaId: request.MediaId,
            MediaType: request.MediaType,
            SourceKey: request.SourceKey,
            Profile: request.Profile,
            ContentType: asset.ContentType,
            Payload: payload,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ContentEncoding: "br",
            SourceRevision: request.SourceRevision,
            Metadata: new Dictionary<string, string>
            {
                ["sourceContentType"] = asset.ContentType,
                ["sourceSizeBytes"] = asset.Payload.LongLength.ToString(CultureInfo.InvariantCulture),
                ["compressedSizeBytes"] = payload.LongLength.ToString(CultureInfo.InvariantCulture),
                ["profile"] = request.Profile.ToString()
            });

        return [artifact];
    }

    private static void EnsurePagedRequest(CompressionRequest request)
    {
        if (request.MediaType != MediaType.Paged)
        {
            throw new InvalidOperationException(
                $"Paged media compression adapter cannot handle media type '{request.MediaType}'.");
        }
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            stream.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static string CreateArtifactId(CompressionRequest request)
    {
        var fingerprint = string.Join(
            '|',
            request.MediaId.Value,
            request.MediaType,
            request.Profile,
            request.SourceRevision ?? string.Empty,
            request.SourceKey);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash);
    }
}