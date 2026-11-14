using System.Globalization;
using System.Text;

using EMMA.Domain;

// A StreamResponse is handed to the caller inside an Outcome and disposed there; the analyzers cannot follow that hand-over.
#pragma warning disable CA2000, IDISP004
namespace EMMA.Application.Streaming;

/// <summary>Playlists and manifests: fetch, remember, rewrite, repair.</summary>
public sealed partial class StreamProxy
{
    private const string HlsMime = "application/vnd.apple.mpegurl";
    private const string DashMime = "application/dash+xml";
    private const int MaxExtensionLength = 9;

    // A manifest that was inline or plugin-served has no address, so what is relative in it leads nowhere.
    private static readonly Uri Detached = new("http://detached.invalid/");

    private static StreamResponse Text(string mime, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new StreamResponse(mime, new MemoryStream(bytes, writable: false), bytes.Length, rangeStart: null, total: null, acceptsRanges: false);
    }

    /// <summary>
    /// The file extension of what a part leads to, kept on its address. Nothing here needs it (a part is found by its number),
    /// but a strict HLS client such as ffmpeg refuses a segment whose address does not end in a known one.
    /// </summary>
    private static string ExtensionOf(Uri url)
    {
        string extension = Path.GetExtension(url.AbsolutePath);
        return extension.Length is > 1 and <= MaxExtensionLength && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : string.Empty;
    }

    private async Task<Outcome<StreamResponse>> ServeManifestAsync(StreamSession session, int part, StreamTarget target, string sessionPath, CancellationToken cancellationToken)
    {
        Outcome<AssetPayload> fetched = await this.FetchManifestAsync(session, target, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess
            && part == 0
            && target.Delivery is RemoteDelivery
            && fetched.Error.Code is ErrorCode.NotFound or ErrorCode.Unauthorized
            && await this.RefreshRootAsync(session, target, cancellationToken).ConfigureAwait(false) is { } fresh)
        {
            target = fresh;
            fetched = await this.FetchManifestAsync(session, target, cancellationToken).ConfigureAwait(false);
        }

        RemoteDelivery? origin = target.Delivery as RemoteDelivery;
        AssetKey key = ManifestKey(session, origin?.Url);
        AssetPayload payload;
        bool repaired = false;
        if (fetched.IsSuccess)
        {
            payload = fetched.Value;
            await cache.SetAsync(key, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (fetched.Error.Code != ErrorCode.Cancelled && await cache.GetAsync(key, cancellationToken).ConfigureAwait(false) is { } remembered)
        {
            // Upstream cannot be reached, so the player gets the last playlist it saw, cut down to what the cache can still serve.
            payload = remembered;
            repaired = true;
        }
        else
        {
            return Outcome.Failure<StreamResponse>(fetched.Error);
        }

        string text = Encoding.UTF8.GetString(payload.Payload.Span);
        Uri baseUri = origin?.Url ?? Detached;
        if (HlsPlaylist.Looks(text))
        {
            HashSet<HlsReference>? available = repaired ? this.AvailableIn(session, text, baseUri) : null;
            string rewritten = HlsPlaylist.Transform(
                text,
                baseUri,
                reference => this.Register(session, reference.IsPlaylist ? StreamPartKind.Manifest : StreamPartKind.Segment, reference.Url, origin),
                available is null ? null : available.Contains);
            return Outcome.Success(Text(HlsMime, rewritten));
        }

        string? dash = DashManifest.Looks(text)
            ? DashManifest.Rewrite(text, baseUri, sessionPath, directory => this.Register(session, StreamPartKind.Directory, directory, origin) + "/")
            : null;
        return dash is null
            ? Outcome.Failure<StreamResponse>(ErrorCode.Unsupported, "The stream's manifest is neither an HLS playlist nor a DASH manifest the host can serve.")
            : Outcome.Success(Text(DashMime, dash));
    }

    private async Task<Outcome<AssetPayload>> FetchManifestAsync(StreamSession session, StreamTarget target, CancellationToken cancellationToken)
    {
        switch (target.Delivery)
        {
            case InlineDelivery inline:
                return Outcome.Success(new AssetPayload(target.Mime, inline.Payload, time.GetUtcNow()));
            case RemoteDelivery remote:
                CapabilityPolicy policy = policies.GetPolicy(session.Node.Plugin);
                return await executor
                    .ExecuteAsync(token => fetcher.FetchAsync(remote, policy, options.MaxManifestBytes, token), cancellationToken)
                    .ConfigureAwait(false);
            default:
                return await assets
                    .GetPayloadAsync(session.Node, session.Role, new Asset(target.Mime, target.Delivery, null, []), cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Finds which of the things a playlist points at the cache can still serve.</summary>
    private HashSet<HlsReference> AvailableIn(StreamSession session, string text, Uri baseUri) =>
        [.. HlsPlaylist.References(text, baseUri).Where(reference =>
            cache.Contains(reference.IsPlaylist ? ManifestKey(session, reference.Url) : SegmentKey(session, reference.Url)))];

    /// <summary>Gives something a playlist points at an address of its own beneath the session and returns what to write in its place.</summary>
    private string Register(StreamSession session, StreamPartKind kind, Uri url, RemoteDelivery? parent)
    {
        IReadOnlyList<HttpHeader> headers = parent is null ? [] : HeadersFor(parent, url);
        string mime = kind == StreamPartKind.Manifest ? HlsMime : SegmentMime;
        return session.Register(kind, url, headers, mime, options.MaxPartsPerSession) is { } part
            ? part.ToString(CultureInfo.InvariantCulture) + ExtensionOf(url)
            : url.AbsoluteUri;
    }
}
