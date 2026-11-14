using System.Runtime.InteropServices;

using EMMA.Application.Ports;
using EMMA.Domain;

// A StreamResponse is handed to the caller inside an Outcome and disposed there; the analyzers cannot follow that hand-over.
#pragma warning disable CA2000, IDISP004
namespace EMMA.Application.Streaming;

/// <summary>Media bytes: direct files, segments, and what only the plugin can read.</summary>
public sealed partial class StreamProxy
{
    /// <summary>Answers with some bytes already in memory, or a range of them.</summary>
    private static StreamResponse Slice(string mime, ReadOnlyMemory<byte> bytes, ByteRange? range)
    {
        if (range is null)
        {
            return new StreamResponse(mime, StreamOf(bytes), bytes.Length, rangeStart: null, total: null, acceptsRanges: true);
        }

        // Past the end is an empty answer, which the hosts turn into "range not satisfiable".
        int start = (int)Math.Min(range.Start, bytes.Length);
        int length = (int)Math.Min(range.Length ?? bytes.Length, bytes.Length - start);
        return new StreamResponse(mime, StreamOf(bytes.Slice(start, length)), length, range.Start, bytes.Length, acceptsRanges: true);
    }

    private static MemoryStream StreamOf(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    private async Task<Outcome<StreamResponse>> ServeMediaAsync(StreamSession session, int part, StreamTarget target, ByteRange? range, CancellationToken cancellationToken)
    {
        Outcome<StreamResponse> served = await this.ServeOnceAsync(session, target, range, cancellationToken).ConfigureAwait(false);
        if (!served.IsSuccess
            && part == 0
            && target.Delivery is not InlineDelivery
            && served.Error.Code is ErrorCode.NotFound or ErrorCode.Unauthorized
            && await this.RefreshRootAsync(session, target, cancellationToken).ConfigureAwait(false) is { } fresh)
        {
            return await this.ServeOnceAsync(session, fresh, range, cancellationToken).ConfigureAwait(false);
        }

        return served;
    }

    private Task<Outcome<StreamResponse>> ServeOnceAsync(StreamSession session, StreamTarget target, ByteRange? range, CancellationToken cancellationToken) => target.Delivery switch
    {
        InlineDelivery inline => Task.FromResult(Outcome.Success(Slice(target.Mime, inline.Payload, range))),
        ProxiedDelivery proxied => this.ServeProxiedAsync(session, target, proxied, range, cancellationToken),
        RemoteDelivery remote => this.ServeRemoteAsync(session, target, remote, range, cancellationToken),
        _ => Task.FromResult(Outcome.Failure<StreamResponse>(ErrorCode.Internal, "Unknown asset delivery.")),
    };

    private async Task<Outcome<StreamResponse>> ServeRemoteAsync(StreamSession session, StreamTarget target, RemoteDelivery remote, ByteRange? range, CancellationToken cancellationToken)
    {
        // A segment is small and asked for again (a seek back, a second player), so it is kept whole. A direct file is not.
        bool keepWhole = target.Kind != StreamPartKind.File;
        AssetKey key = SegmentKey(session, remote.Url);
        if (keepWhole && await cache.GetAsync(key, cancellationToken).ConfigureAwait(false) is { } hit)
        {
            return Outcome.Success(Slice(hit.Mime, hit.Payload, range));
        }

        CapabilityPolicy policy = policies.GetPolicy(session.Node.Plugin);

        // A player asks for a segment with a range (ffmpeg sends "bytes=0-" for every one), so a ranged request is no reason
        // not to fetch the whole piece: it is small, it is cached for the next request, and the range is cut from it.
        Outcome<RemoteResponse> opened = await this.OpenAsync(remote, policy, keepWhole ? null : range, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Outcome.Failure<StreamResponse>(opened.Error);
        }

        RemoteResponse upstream = opened.Value;
        if (keepWhole && (upstream.Length is not { } known || known <= 0 || known > options.MaxSegmentBytes))
        {
            // Too large to keep (or of unknown size): do what a direct file does.
            await upstream.DisposeAsync().ConfigureAwait(false);
            keepWhole = false;
            opened = await this.OpenAsync(remote, policy, range, cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess)
            {
                return Outcome.Failure<StreamResponse>(opened.Error);
            }

            upstream = opened.Value;
        }

        string mime = target.Kind == StreamPartKind.File ? target.Mime : upstream.Mime;
        if (!keepWhole)
        {
            return Outcome.Success(new StreamResponse(mime, upstream.Body, upstream.Length, upstream.RangeStart, upstream.Total, upstream.AcceptsRanges, upstream));
        }

        return Outcome.Success(await this.ReadWholeAsync(upstream, key, mime, range, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads a whole piece, keeps it in the cache, and answers with the part of it that was asked for.</summary>
    private async Task<StreamResponse> ReadWholeAsync(RemoteResponse upstream, AssetKey key, string mime, ByteRange? range, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = new byte[upstream.Length!.Value];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = await upstream.Body.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read == bytes.Length)
            {
                await cache.SetAsync(key, new AssetPayload(mime, bytes, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }

            return Slice(mime, bytes.AsMemory(0, read), range);
        }
        finally
        {
            // The response was handed over to be read and released here.
#pragma warning disable IDISP007
            await upstream.DisposeAsync().ConfigureAwait(false);
#pragma warning restore IDISP007
        }
    }

    private Task<Outcome<RemoteResponse>> OpenAsync(RemoteDelivery remote, CapabilityPolicy policy, ByteRange? range, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(token => fetcher.OpenAsync(remote, policy, range, token), cancellationToken);

    private async Task<Outcome<StreamResponse>> ServeProxiedAsync(StreamSession session, StreamTarget target, ProxiedDelivery proxied, ByteRange? range, CancellationToken cancellationToken)
    {
        long start = range?.Start ?? 0;
        long firstLength = Math.Min(options.ProxiedChunkBytes, range?.Length ?? long.MaxValue);
        Outcome<Chunk> first = await assets.ReadRangeAsync(session.Node, proxied, new ByteRange(start, firstLength), cancellationToken).ConfigureAwait(false);
        if (!first.IsSuccess)
        {
            return Outcome.Failure<StreamResponse>(first.Error);
        }

        long? total = first.Value.Total;
        if (range is not null && total is { } size && start >= size)
        {
            return Outcome.Success(new StreamResponse(target.Mime, Stream.Null, length: 0, start, total, acceptsRanges: true));
        }

        long? end = range?.Length is { } wanted ? start + wanted : null;
        if (total is { } known)
        {
            end = Math.Min(end ?? known, known);
        }

        ProxiedStream body = new(
            (next, token) => assets.ReadRangeAsync(session.Node, proxied, next, token),
            first.Value.Bytes,
            start,
            end,
            options.ProxiedChunkBytes);
        return Outcome.Success(new StreamResponse(target.Mime, body, end - start, range is null ? null : start, total, acceptsRanges: true));
    }
}
