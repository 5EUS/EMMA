using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Streaming;
using EMMA.Domain;

namespace EMMA.Runtime;

/// <summary>
/// A minimal HTTP/1.1 server on the loopback interface that hands a <see cref="StreamProxy"/> to a player in the same
/// process or on the same device. An app that embeds EMMA has no web server, and a media player can only be given an
/// address, so this is that address. It answers <c>GET</c> and <c>HEAD</c> with byte ranges and nothing else, closes the
/// connection after each answer, and only talks to callers that know the secret in the path (a web page cannot guess it,
/// whatever it does with DNS).
/// </summary>
/// <param name="proxy">Answers the requests.</param>
internal sealed class LoopbackStreamServer(StreamProxy proxy) : IAsyncDisposable
{
    private const int MaxHeadBytes = 16 * 1024;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(16);
    private TcpListener? listener;
    private Task? accepting;
    private Uri? root;
    private int disposed;

    /// <summary>Starts listening, if that has not happened yet.</summary>
    /// <returns>The address every stream path is appended to, such as <c>http://127.0.0.1:5000/secret/</c>.</returns>
    public Uri Start()
    {
        lock (this.gate)
        {
            if (this.root is not null)
            {
                return this.root;
            }

            TcpListener started = new(IPAddress.Loopback, 0);
            started.Start();
            this.listener = started;
            int port = ((IPEndPoint)started.LocalEndpoint).Port;
            this.root = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/{Convert.ToHexStringLower(this.secret)}/");
            this.accepting = Task.Run(() => this.AcceptAsync(started, this.stopping.Token));
            return this.root;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // A runtime may be disposed twice; the second time there is nothing left to stop.
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.stopping.CancelAsync().ConfigureAwait(false);
        this.listener?.Stop();
        this.listener?.Dispose();
        if (this.accepting is not null)
        {
            await this.accepting.ConfigureAwait(false);
        }

        this.stopping.Dispose();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        502 => "Bad Gateway",
        _ => "Error",
    };

    private static int StatusOf(ErrorCode code) => code switch
    {
        ErrorCode.NotFound => 404,
        ErrorCode.Unauthorized => 403,
        ErrorCode.RateLimited => 429,
        ErrorCode.Unavailable => 502,
        ErrorCode.InvalidArgument => 400,
        _ => 502,
    };

    /// <summary>
    /// Resolves <c>.</c> and <c>..</c> segments the way an HTTP server does (RFC 3986 section 5.2.4). A player resolves the
    /// relative addresses in a manifest itself, but some (ffmpeg) send them as they are: <c>1/../2/file</c> is the
    /// manifest's way of reaching a sibling directory. Done before anything is looked up, so a path that climbs out of
    /// the secret and session is simply not one of ours.
    /// </summary>
    /// <param name="segments">The path's segments, still percent-encoded.</param>
    /// <returns>The segments with those resolved, or <see langword="null"/> when a <c>..</c> would climb above the first segment.</returns>
    private static string[]? CollapseDotSegments(string[] segments)
    {
        List<string> kept = [];
        foreach (string segment in segments)
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (kept.Count == 0)
                {
                    return null;
                }

                kept.RemoveAt(kept.Count - 1);
                continue;
            }

            kept.Add(segment);
        }

        return [.. kept];
    }

    /// <summary>Reads one <c>bytes=start-</c> or <c>bytes=start-end</c> range; anything else is ignored and the whole part is sent.</summary>
    private static ByteRange? ParseRange(string? value)
    {
        if (value is null || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = value["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start))
        {
            return null;
        }

        if (parts[1].Length == 0)
        {
            return new ByteRange(start, null);
        }

        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long end) && end >= start
            ? new ByteRange(start, end - start + 1)
            : null;
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HeadTimeout);
        byte[] buffer = new byte[MaxHeadBytes];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            used += read;
            int end = Encoding.ASCII.GetString(buffer, 0, used).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end);
            }
        }

        return null;
    }

    private static async Task WriteHeadAsync(Stream stream, int status, string? mime, long? length, string? extra, CancellationToken cancellationToken)
    {
        StringBuilder head = new();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {Reason(status)}\r\n");
        if (mime is not null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {SafeMime(mime)}\r\n");
        }

        if (length is { } known)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {known}\r\n");
        }

        head.Append(extra);
        head.Append("X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
    }

    // The type came from a plugin: keep it to one line of ordinary characters.
    private static string SafeMime(string mime) =>
        mime.Length <= 255 && mime.All(c => c is > ' ' and < '\u007f') && mime.Contains('/', StringComparison.Ordinal) ? mime : "application/octet-stream";

    private static async Task WriteAnswerAsync(Stream stream, StreamResponse response, bool headOnly, CancellationToken cancellationToken)
    {
        if (response.RangeNotSatisfiable)
        {
            string unsatisfied = string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes */{response.Total?.ToString(CultureInfo.InvariantCulture) ?? "*"}\r\n");
            await WriteHeadAsync(stream, 416, null, 0, unsatisfied, cancellationToken).ConfigureAwait(false);
            return;
        }

        StringBuilder extra = new();
        if (response.AcceptsRanges)
        {
            extra.Append("Accept-Ranges: bytes\r\n");
        }

        int status = 200;
        if (response.RangeStart is { } start)
        {
            status = 206;
            string total = response.Total?.ToString(CultureInfo.InvariantCulture) ?? "*";
            long last = start + (response.Length ?? 1) - 1;
            extra.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{last}/{total}\r\n");
        }

        await WriteHeadAsync(stream, status, response.Mime, response.Length, extra.ToString(), cancellationToken).ConfigureAwait(false);
        if (!headOnly)
        {
            await response.Body.CopyToAsync(stream, 81920, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => this.HandleAsync(client, cancellationToken), cancellationToken);
        }
    }

    // The accept loop hands each connection over to be closed here.
#pragma warning disable IDISP007
    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    string? head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (head is not null)
                    {
                        await this.AnswerAsync(stream, head, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The player went away mid-answer (a seek does this all the time), or the runtime is stopping.
            }
        }
    }

#pragma warning restore IDISP007

    private async Task AnswerAsync(Stream stream, string head, CancellationToken cancellationToken)
    {
        string[] lines = head.Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length != 3 || (!string.Equals(request[0], "GET", StringComparison.Ordinal) && !string.Equals(request[0], "HEAD", StringComparison.Ordinal)))
        {
            await WriteHeadAsync(stream, 405, null, 0, "Allow: GET, HEAD\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        StreamRequest? parsed = this.ParseTarget(request[1], headers);
        if (parsed is null)
        {
            await WriteHeadAsync(stream, 404, null, 0, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        Outcome<StreamResponse> outcome = await proxy.ServeAsync(parsed, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            await WriteHeadAsync(stream, StatusOf(outcome.Error.Code), null, 0, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The outcome handed the response over; this is where it ends.
#pragma warning disable IDISP007
        await using (outcome.Value.ConfigureAwait(false))
#pragma warning restore IDISP007
        {
            await WriteAnswerAsync(stream, outcome.Value, string.Equals(request[0], "HEAD", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks the secret and turns the path and range into a request; <see langword="null"/> when this is not a request the server answers.</summary>
    private StreamRequest? ParseTarget(string target, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Host", out string? host) || !string.Equals(host, $"127.0.0.1:{((IPEndPoint?)this.listener?.LocalEndpoint)?.Port}", StringComparison.Ordinal))
        {
            return null;
        }

        int question = target.IndexOf('?', StringComparison.Ordinal);
        string path = question < 0 ? target : target[..question];
        string? query = question < 0 ? null : target[(question + 1)..];
        string[]? segments = CollapseDotSegments(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (segments is null || segments.Length < 3 || !this.FixedTimeEquals(segments[0]))
        {
            return null;
        }

        List<string> decoded = [];
        foreach (string segment in segments.Skip(1))
        {
            string value = Uri.UnescapeDataString(segment);
            if (value.Contains('/', StringComparison.Ordinal))
            {
                return null;
            }

            decoded.Add(value);
        }

        headers.TryGetValue("Range", out string? range);
        return new StreamRequest(string.Join('/', decoded), ParseRange(range), query, $"http://127.0.0.1:{((IPEndPoint?)this.listener?.LocalEndpoint)?.Port}/{segments[0]}/");
    }

    private bool FixedTimeEquals(string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(Convert.ToHexStringLower(this.secret)));
}
