using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Streaming;

/// <summary>
/// Gives a player a local address for media that cannot be played from where it lives: a stream that needs headers or
/// cookies, a link only the plugin can read, a playlist whose segments are on another host. The player asks the host,
/// the host asks upstream under the plugin's network policy, and what comes back is passed on. Playlists are rewritten
/// so every segment comes through here too; segments are cached; when upstream is unreachable a playlist is repaired from
/// what the cache still holds. Nothing here interprets media: it reads manifests and passes bytes.
/// </summary>
/// <param name="assets">Resolves nodes to assets and reads what only a plugin can.</param>
/// <param name="policies">What each plugin may reach.</param>
/// <param name="fetcher">Fetches remote bytes.</param>
/// <param name="cache">Holds segments and playlists.</param>
/// <param name="downloads">Serves a stored download before the plugin is asked.</param>
/// <param name="executor">Runs upstream calls with retries and timeouts.</param>
/// <param name="options">Limits.</param>
/// <param name="time">The time source.</param>
public sealed partial class StreamProxy(
    AssetPipeline assets,
    ICapabilityPolicyProvider policies,
    IAssetFetcherPort fetcher,
    IAssetCachePort cache,
    DownloadPipeline downloads,
    PluginCallExecutor executor,
    StreamProxyOptions options,
    TimeProvider time)
{
    private const string SegmentMime = "application/octet-stream";

    private static readonly string[] CredentialHeaders = ["Authorization", "Cookie", "Proxy-Authorization"];

    private readonly Lock gate = new();
    private readonly Dictionary<string, StreamSession> sessions = new(StringComparer.Ordinal);

    /// <summary>Opens one asset of a node for playing and gives it an address.</summary>
    /// <param name="node">The node.</param>
    /// <param name="role">The role to resolve, usually <c>stream</c> or <c>audio</c>.</param>
    /// <param name="index">Which of the resolved assets to open.</param>
    /// <param name="hint">A quality preference passed to the plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where to ask for the stream, or why it could not be opened.</returns>
    public async Task<Outcome<StreamLink>> OpenAsync(NodeRef node, AssetRole role, int index, string? hint, CancellationToken cancellationToken)
    {
        IReadOnlyList<Prop> hints = AssetHint.ToProps(hint);
        StreamTarget root;
        if (index == 0 && await downloads.ReadStoredAsync(node, role, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            root = new StreamTarget(StreamPartKind.File, new InlineDelivery(stored.Payload), stored.Mime);
        }
        else
        {
            Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(node, role, hints, cancellationToken).ConfigureAwait(false);
            if (!resolved.IsSuccess)
            {
                return Outcome.Failure<StreamLink>(resolved.Error);
            }

            if (index < 0 || index >= resolved.Value.Count)
            {
                return Outcome.Failure<StreamLink>(ErrorCode.NotFound, "The node has no asset at that index.");
            }

            root = TargetOf(resolved.Value[index]);
        }

        string id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        StreamSession session = new(id, node, role, index, hints, root, time.GetUtcNow());
        lock (this.gate)
        {
            this.Evict();
            this.sessions[id] = session;
        }

        return Outcome.Success(new StreamLink(id, $"{id}/0", root.Mime));
    }

    /// <summary>Answers a player's request for part of an opened stream.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer, or why there is none. The caller disposes it.</returns>
    public async Task<Outcome<StreamResponse>> ServeAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        string[] segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // A part is named by its number; what follows a dot is only the file extension a strict player wants to see.
        string number = segments.Length > 1 ? segments[1].Split('.')[0] : string.Empty;
        if (segments.Length < 2
            || !int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int part))
        {
            return Outcome.Failure<StreamResponse>(ErrorCode.NotFound, "There is no such stream.");
        }

        StreamSession? session = this.Touch(segments[0]);
        StreamTarget? target = session?.Find(part);
        if (session is null || target is null)
        {
            return Outcome.Failure<StreamResponse>(ErrorCode.NotFound, "There is no such stream.");
        }

        if (target.Kind == StreamPartKind.Directory)
        {
            Outcome<StreamTarget> beneath = Beneath(target, segments.AsSpan(2), request.Query);
            if (!beneath.IsSuccess)
            {
                return Outcome.Failure<StreamResponse>(beneath.Error);
            }

            target = beneath.Value;
        }
        else if (segments.Length > 2)
        {
            return Outcome.Failure<StreamResponse>(ErrorCode.NotFound, "There is no such stream.");
        }

        return target.Kind == StreamPartKind.Manifest
            ? await this.ServeManifestAsync(session, part, target, cancellationToken).ConfigureAwait(false)
            : await this.ServeMediaAsync(session, part, target, request.Range, cancellationToken).ConfigureAwait(false);
    }

    private static StreamTarget TargetOf(Asset asset) => new(
        IsManifest(asset) ? StreamPartKind.Manifest : StreamPartKind.File,
        asset.Delivery,
        asset.Mime);

    private static bool IsManifest(Asset asset)
    {
        string mime = asset.Mime;
        if (mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string path = asset.Delivery is RemoteDelivery remote ? remote.Url.AbsolutePath : string.Empty;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Works out what a path beneath a directory part leads to: the directory's address with the rest appended.</summary>
    private static Outcome<StreamTarget> Beneath(StreamTarget directory, ReadOnlySpan<string> rest, string? query)
    {
        if (directory.Delivery is not RemoteDelivery origin || rest.IsEmpty || Array.Exists(rest.ToArray(), IsUnsafeSegment))
        {
            return Outcome.Failure<StreamTarget>(ErrorCode.NotFound, "There is no such stream.");
        }

        UriBuilder builder = new(origin.Url)
        {
            Path = origin.Url.AbsolutePath + string.Join('/', rest.ToArray().Select(Uri.EscapeDataString)),
            Query = query ?? string.Empty,
        };
        return Outcome.Success(new StreamTarget(StreamPartKind.Segment, new RemoteDelivery(builder.Uri, origin.Headers, ExpiresAt: null), SegmentMime));
    }

    private static bool IsUnsafeSegment(string segment) =>
        segment is "." or ".." || segment.Contains('\\', StringComparison.Ordinal) || segment.Any(char.IsControl);

    /// <summary>The headers a request for something a playlist pointed at carries: the playlist's, less credentials when it leaves the host they were for.</summary>
    private static IReadOnlyList<HttpHeader> HeadersFor(RemoteDelivery parent, Uri child) =>
        string.Equals(parent.Url.IdnHost, child.IdnHost, StringComparison.OrdinalIgnoreCase)
            ? parent.Headers
            : [.. parent.Headers.Where(header => !CredentialHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase))];

    private static string VariantOf(Uri url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));

    private static AssetKey SegmentKey(StreamSession session, Uri url) => new(session.Node, session.Role, VariantOf(url));

    private static AssetKey ManifestKey(StreamSession session, Uri? url) => new(session.Node, session.Role, "m:" + (url is null ? "root" : VariantOf(url)));

    private StreamSession? Touch(string id)
    {
        lock (this.gate)
        {
            if (!this.sessions.TryGetValue(id, out StreamSession? session))
            {
                return null;
            }

            DateTimeOffset now = time.GetUtcNow();
            if (now - session.LastUsed > options.SessionLifetime)
            {
                this.sessions.Remove(id);
                return null;
            }

            session.LastUsed = now;
            return session;
        }
    }

    // The caller holds the lock.
    private void Evict()
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (string expired in this.sessions.Where(pair => now - pair.Value.LastUsed > options.SessionLifetime).Select(pair => pair.Key).ToList())
        {
            this.sessions.Remove(expired);
        }

        while (this.sessions.Count >= options.MaxSessions)
        {
            this.sessions.Remove(this.sessions.MinBy(pair => pair.Value.LastUsed).Key);
        }
    }

    /// <summary>
    /// Asks the plugin for the asset again after a link died before its stated expiry, but only for a stream's first part and
    /// only when the plugin now answers differently: the same link would only fail the same way.
    /// </summary>
    private async Task<StreamTarget?> RefreshRootAsync(StreamSession session, StreamTarget failed, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<Asset>> fresh = await assets.ResolveAsync(session.Node, session.Role, session.Hints, CacheMode.Refresh, cancellationToken).ConfigureAwait(false);
        if (!fresh.IsSuccess || session.Index >= fresh.Value.Count)
        {
            return null;
        }

        StreamTarget replacement = TargetOf(fresh.Value[session.Index]);
        if (replacement.Delivery.Equals(failed.Delivery))
        {
            return null;
        }

        session.ReplaceRoot(replacement);
        return replacement;
    }
}
