using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// Remembers the nodes the host has seen. Nodes are a cache of what plugins said, keyed by
/// (plugin, local id) exactly like <see cref="NodeRef"/>, so identical local ids under
/// different plugins never collide. A node's parent is always in the same plugin.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="time">The time source that stamps each write.</param>
public sealed class SqliteNodeStore(StorageOptions options, TimeProvider time) : INodeStorePort
{
    private const string Upsert = @"
        INSERT INTO nodes (plugin_id, local_id, kind, title, parent_local_id, thumbnail, props, updated_at, detailed)
        VALUES ($plugin, $local, $kind, $title, $parent, $thumbnail, $props, $updatedAt, $detailed)
        ON CONFLICT (plugin_id, local_id) DO UPDATE SET
            detailed = CASE
                WHEN excluded.detailed = 1 THEN 1
                WHEN nodes.kind = excluded.kind AND nodes.title = excluded.title AND nodes.thumbnail IS excluded.thumbnail
                    AND nodes.props = excluded.props AND nodes.parent_local_id IS excluded.parent_local_id THEN nodes.detailed
                ELSE 0
            END,
            kind = excluded.kind,
            title = excluded.title,
            parent_local_id = excluded.parent_local_id,
            thumbnail = excluded.thumbnail,
            props = excluded.props,
            updated_at = excluded.updated_at;
    ";

    private const string Select = @"
        SELECT plugin_id, local_id, kind, title, parent_local_id, thumbnail, props
        FROM nodes
        WHERE plugin_id = $plugin AND local_id = $local;
    ";

    private const string SelectDetailed = @"
        SELECT plugin_id, local_id, kind, title, parent_local_id, thumbnail, props
        FROM nodes
        WHERE plugin_id = $plugin AND local_id = $local AND detailed = 1;
    ";

    private const string SelectChildIds = @"
        SELECT local_id
        FROM nodes
        WHERE plugin_id = $plugin AND parent_local_id = $parent;
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A node names a parent in a different plugin.</exception>
    public Task UpsertAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) => this.StoreAsync(nodes, detailed: false, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A node names a parent in a different plugin.</exception>
    public Task UpsertDetailedAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) => this.StoreAsync(nodes, detailed: true, cancellationToken);

    /// <inheritdoc/>
    public Task<Node?> GetAsync(NodeRef node, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            Select,
            parameters =>
            {
                parameters.Bind("$plugin", node.Plugin.Value);
                parameters.Bind("$local", node.Id.Value);
            },
            ReadNode,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Node>> GetManyAsync(IReadOnlyList<NodeRef> nodes, bool detailedOnly, CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        List<Node> found = [];
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            foreach (NodeRef reference in nodes)
            {
                IReadOnlyList<Node> rows = await SqliteDatabase.QueryAsync(
                    connection,
                    detailedOnly ? SelectDetailed : Select,
                    parameters =>
                    {
                        parameters.Bind("$plugin", reference.Plugin.Value);
                        parameters.Bind("$local", reference.Id.Value);
                    },
                    ReadNode,
                    cancellationToken).ConfigureAwait(false);
                found.AddRange(rows);
            }
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<LocalNodeId>> ChildIdsAsync(NodeRef parent, CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalNodeId> ids = await this.database.QueryAsync(
            SelectChildIds,
            parameters =>
            {
                parameters.Bind("$plugin", parent.Plugin.Value);
                parameters.Bind("$parent", parent.Id.Value);
            },
            reader => LocalNodeId.TryCreate(reader.GetString(0), out LocalNodeId id)
                ? id
                : throw new InvalidDataException("A stored node has an invalid local id."),
            cancellationToken).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    private static Node ReadNode(SqliteDataReader reader)
    {
        PluginId plugin = PluginId.TryCreate(reader.GetString(0), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A stored node has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(1), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException("A stored node has an invalid local id.");
        NodeKind kind = NodeKind.TryCreate(reader.GetString(2), out NodeKind nodeKind)
            ? nodeKind
            : throw new InvalidDataException($"The stored node '{plugin}:{local}' has an invalid kind.");

        string? parentText = reader.GetNullableString(4);
        NodeRef? parent = null;
        if (parentText is not null)
        {
            parent = LocalNodeId.TryCreate(parentText, out LocalNodeId parentId)
                ? new NodeRef(plugin, parentId)
                : throw new InvalidDataException($"The stored node '{plugin}:{local}' has an invalid parent id.");
        }

        string? thumbnailText = reader.GetNullableString(5);
        Uri? thumbnail = thumbnailText is null ? null : new Uri(thumbnailText, UriKind.Absolute);

        return new Node(new NodeRef(plugin, local), kind, reader.GetString(3), parent, thumbnail, PropCodec.Decode(reader.GetString(6)));
    }

    private async Task StoreAsync(IReadOnlyList<Node> nodes, bool detailed, CancellationToken cancellationToken)
    {
        foreach (Node node in nodes)
        {
            if (node.Parent is { } parent && parent.Plugin != node.Ref.Plugin)
            {
                throw new ArgumentException($"Node '{node.Ref}' names a parent in another plugin.", nameof(nodes));
            }
        }

        if (nodes.Count == 0)
        {
            return;
        }

        long now = SqliteValues.ToStored(time.GetUtcNow());
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // One transaction: a batch is stored whole or not at all.
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (Node node in nodes)
                {
                    await SqliteDatabase.ExecuteAsync(
                        connection,
                        Upsert,
                        parameters =>
                        {
                            parameters.Bind("$plugin", node.Ref.Plugin.Value);
                            parameters.Bind("$local", node.Ref.Id.Value);
                            parameters.Bind("$kind", node.Kind.Value);
                            parameters.Bind("$title", node.Title);
                            parameters.Bind("$parent", node.Parent?.Id.Value);
                            parameters.Bind("$thumbnail", node.Thumbnail?.AbsoluteUri);
                            parameters.Bind("$props", PropCodec.Encode(node.Props));
                            parameters.Bind("$updatedAt", now);
                            parameters.Bind("$detailed", detailed ? 1 : 0);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
