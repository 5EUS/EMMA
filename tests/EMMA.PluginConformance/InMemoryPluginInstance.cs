using EMMA.Domain;
using EMMA.PluginHost.Instances;

namespace EMMA.PluginConformance;

/// <summary>
/// An <see cref="IPluginInstance"/> that serves <see cref="FixtureCatalog"/> from memory. It is the reference for
/// the contract's behavior, and what code above the seam can be tested against without any transport.
/// Search text selects behaviors the way the fixture plugin does: <c>trap</c> reports a crash and
/// <c>error:&lt;code&gt;</c> fails with that code.
/// </summary>
public sealed class InMemoryPluginInstance : IPluginInstance
{
    private static readonly IReadOnlyList<PluginLogEntry> NoLogs = [];

    private readonly bool searchAssist;

    /// <summary>Initializes a new instance of the <see cref="InMemoryPluginInstance"/> class.</summary>
    /// <param name="searchAssist">Whether to offer the search-assist extension.</param>
    public InMemoryPluginInstance(bool searchAssist = false)
    {
        this.searchAssist = searchAssist;
        this.Extensions = searchAssist
            ? new HashSet<string>(StringComparer.Ordinal) { PluginExtensions.SearchAssist }
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Gets how many calls were made, whatever they were.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc/>
    public IReadOnlySet<string> Extensions { get; }

    /// <inheritdoc/>
    public Task<PluginCall<PluginInfo>> DescribeAsync(CancellationToken cancellationToken) =>
        this.Done(Outcome.Success(FixtureCatalog.Info), false, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<PluginHealth>> HealthAsync(CancellationToken cancellationToken) =>
        this.Done(Outcome.Success(new PluginHealth(true, null)), false, cancellationToken);

    /// <inheritdoc/>
    public Task<PluginCall<PluginNodePage>> SearchAsync(Query query, CancellationToken cancellationToken)
    {
        string text = query.Text.Trim();
        if (string.Equals(text, "trap", StringComparison.Ordinal))
        {
            return this.Done(Outcome.Failure<PluginNodePage>(ErrorCode.Internal, "The plugin crashed."), true, cancellationToken);
        }

        if (text.StartsWith("error:", StringComparison.Ordinal))
        {
            return this.Done(Outcome.Failure<PluginNodePage>(Failure(text["error:".Length..])), false, cancellationToken);
        }

        IReadOnlyList<PluginNode> all = text.ToUpperInvariant() switch
        {
            "RECIPE" => FixtureCatalog.Recipes,
            "EPISODE" => FixtureCatalog.Episodes,
            "TRACK" => FixtureCatalog.Tracks,
            _ => FixtureCatalog.Series,
        };
        return this.Done(Page(all, query.Cursor, query.Limit), false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<PluginNodePage>> ChildrenAsync(LocalNodeId? parent, PageRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginNode>? children = FixtureCatalog.ChildrenOf(parent?.Value);
        Outcome<PluginNodePage> outcome = children is null
            ? Outcome.Failure<PluginNodePage>(ErrorCode.NotFound, $"no node {parent}")
            : Page(children, request.Cursor, request.Limit);
        return this.Done(outcome, false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> GetAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken)
    {
        PluginNode[] known = [.. FixtureCatalog.Series, .. FixtureCatalog.Recipes, .. FixtureCatalog.Episodes, .. FixtureCatalog.Tracks];
        IReadOnlyList<PluginNode> found = [.. ids.Select(id => Array.Find(known, node => string.Equals(node.Id, id.Value, StringComparison.Ordinal))).OfType<PluginNode>()];
        return this.Done(Outcome.Success(found), false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<Asset>>> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        string node = request.Node.Value;
        string role = request.Role.Value;
        IReadOnlyList<Asset>? assets = (node, role) switch
        {
            ("inline1", "text") => [new Asset("text/plain", new InlineDelivery("hello"u8.ToArray()), 5, [])],
            ("proxied1", "image") => [new Asset("application/octet-stream", new ProxiedDelivery("tok-1"), FixtureCatalog.ProxiedLength, [])],
            (_, "image") when node.Contains("-p", StringComparison.Ordinal) => [RemotePage(node)],
            _ => FixtureCatalog.PlaybackAssets(node, role),
        };
        Outcome<IReadOnlyList<Asset>> outcome = assets is null
            ? Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Unsupported, "nothing to resolve for that node and role")
            : Outcome.Success(assets);
        return this.Done(outcome, false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<Chunk>> ReadAsync(string token, ByteRange? range, CancellationToken cancellationToken)
    {
        if (!string.Equals(token, "tok-1", StringComparison.Ordinal))
        {
            return this.Done(Outcome.Failure<Chunk>(ErrorCode.NotFound, "unknown token"), false, cancellationToken);
        }

        byte[] data = FixtureCatalog.Pattern(FixtureCatalog.ProxiedLength);
        int start = (int)Math.Min(range?.Start ?? 0, data.Length);
        int length = (int)Math.Min(range?.Length ?? data.Length, data.Length - start);
        return this.Done(Outcome.Success(new Chunk(data.AsMemory(start, length), data.Length)), false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<Choice>>> SuggestAsync(string filterId, string text, int limit, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<Choice>> outcome = this.searchAssist
            ? Outcome.Success<IReadOnlyList<Choice>>([.. Enumerable.Range(1, Math.Min(Math.Max(limit, 0), 5)).Select(i => new Choice($"{filterId}:{text}-{i}", $"{text} {i}", null, false))])
            : Outcome.Failure<IReadOnlyList<Choice>>(ErrorCode.Unsupported, "search-assist is not offered");
        return this.Done(outcome, false, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PluginCall<IReadOnlyList<PluginNode>>> EnrichAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<PluginNode>> outcome = this.searchAssist
            ? Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => new PluginNode(id.Value, "series", $"Enriched {id}", null, null, [new Prop("emma.rating", PropValue.FromNumber(9.0))]))])
            : Outcome.Failure<IReadOnlyList<PluginNode>>(ErrorCode.Unsupported, "search-assist is not offered");
        return this.Done(outcome, false, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PluginError Failure(string code) => code switch
    {
        "not-found" => new PluginError(ErrorCode.NotFound, "requested failure"),
        "unauthorized" => new PluginError(ErrorCode.Unauthorized, "requested failure"),
        "rate-limited" => new PluginError(ErrorCode.RateLimited, "slow down", TimeSpan.FromMilliseconds(1500)),
        "unavailable" => new PluginError(ErrorCode.Unavailable, "requested failure"),
        "invalid-argument" => new PluginError(ErrorCode.InvalidArgument, "requested failure"),
        "unsupported" => new PluginError(ErrorCode.Unsupported, "requested failure"),
        _ => new PluginError(ErrorCode.Internal, "requested failure"),
    };

    private static Asset RemotePage(string node) => new(
        "image/png",
        new RemoteDelivery(new UriBuilder("https", "files.example.com", 443, $"{node}.png").Uri, [new HttpHeader("referer", "https://example.com/")], null),
        null,
        [new Prop("emma.quality", PropValue.FromText("original"))]);

    private static Outcome<PluginNodePage> Page(IReadOnlyList<PluginNode> all, string? cursor, int? limit)
    {
        int start = 0;
        if (cursor is not null && !int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start))
        {
            return Outcome.Failure<PluginNodePage>(ErrorCode.InvalidArgument, "bad cursor");
        }

        int size = Math.Clamp(limit ?? 50, 1, 500);
        int end = Math.Min(start + size, all.Count);
        PluginNode[] items = [.. all.Skip(start).Take(Math.Max(end - start, 0))];
        return Outcome.Success(new PluginNodePage(items, end < all.Count ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, all.Count, []));
    }

    private Task<PluginCall<T>> Done<T>(Outcome<T> outcome, bool trapped, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Calls++;
        return Task.FromResult(new PluginCall<T>(outcome, trapped, NoLogs));
    }
}
