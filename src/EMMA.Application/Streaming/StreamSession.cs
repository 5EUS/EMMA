using EMMA.Domain;

namespace EMMA.Application.Streaming;

/// <summary>
/// One opened stream: the node and role it was opened for, and the addresses the host hands a player for it. Part 0 is
/// the asset itself; every other part was registered when a playlist or manifest was rewritten.
/// </summary>
internal sealed class StreamSession
{
    private readonly Lock gate = new();
    private readonly List<StreamTarget> parts = [];
    private readonly Dictionary<(StreamPartKind Kind, string Url), int> byUrl = [];

    /// <summary>Initializes a new instance of the <see cref="StreamSession"/> class.</summary>
    /// <param name="id">The unguessable name of the session.</param>
    /// <param name="node">The node that was opened.</param>
    /// <param name="role">The role it was opened for.</param>
    /// <param name="index">Which of the resolved assets it was.</param>
    /// <param name="hints">The hints it was resolved with.</param>
    /// <param name="root">What part 0 leads to.</param>
    /// <param name="lastUsed">When the session was opened.</param>
    public StreamSession(string id, NodeRef node, AssetRole role, int index, IReadOnlyList<Prop> hints, StreamTarget root, DateTimeOffset lastUsed)
    {
        this.Id = id;
        this.Node = node;
        this.Role = role;
        this.Index = index;
        this.Hints = hints;
        this.parts.Add(root);
        this.LastUsed = lastUsed;
    }

    /// <summary>Gets the name of the session.</summary>
    public string Id { get; }

    /// <summary>Gets the node that was opened.</summary>
    public NodeRef Node { get; }

    /// <summary>Gets the role it was opened for.</summary>
    public AssetRole Role { get; }

    /// <summary>Gets which of the resolved assets it was.</summary>
    public int Index { get; }

    /// <summary>Gets the hints it was resolved with.</summary>
    public IReadOnlyList<Prop> Hints { get; }

    /// <summary>Gets or sets when the session was last used.</summary>
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>Gets what part 0 leads to.</summary>
    public StreamTarget Root
    {
        get
        {
            lock (this.gate)
            {
                return this.parts[0];
            }
        }
    }

    /// <summary>Replaces what part 0 leads to, after the plugin was asked again.</summary>
    /// <param name="root">The new target.</param>
    public void ReplaceRoot(StreamTarget root)
    {
        lock (this.gate)
        {
            this.parts[0] = root;
        }
    }

    /// <summary>Finds a part.</summary>
    /// <param name="part">The part number.</param>
    /// <returns>What it leads to, or <see langword="null"/> when there is no such part.</returns>
    public StreamTarget? Find(int part)
    {
        lock (this.gate)
        {
            return part >= 0 && part < this.parts.Count ? this.parts[part] : null;
        }
    }

    /// <summary>Registers a remote address, or finds it again.</summary>
    /// <param name="kind">How it is served.</param>
    /// <param name="url">The address.</param>
    /// <param name="headers">The headers its fetch carries.</param>
    /// <param name="mime">The type to answer with.</param>
    /// <param name="limit">The most parts a session may hold.</param>
    /// <returns>The part number, or <see langword="null"/> when the session is full.</returns>
    public int? Register(StreamPartKind kind, Uri url, IReadOnlyList<HttpHeader> headers, string mime, int limit)
    {
        lock (this.gate)
        {
            if (this.byUrl.TryGetValue((kind, url.AbsoluteUri), out int existing))
            {
                return existing;
            }

            if (this.parts.Count >= limit)
            {
                return null;
            }

            this.parts.Add(new StreamTarget(kind, new RemoteDelivery(url, headers, ExpiresAt: null), mime));
            this.byUrl[(kind, url.AbsoluteUri)] = this.parts.Count - 1;
            return this.parts.Count - 1;
        }
    }
}
