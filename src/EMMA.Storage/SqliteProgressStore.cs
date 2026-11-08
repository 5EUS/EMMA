using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// Stores user progress with one row shape for every media type: a node, a unit, a value.
/// The database also enforces the rules (non-negative value, total at least the value), so
/// a bug above cannot store nonsense.
/// </summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqliteProgressStore(StorageOptions options) : IProgressPort
{
    private const string Columns = "user_id, plugin_id, local_id, unit, value, total, completed, updated_at";

    private const string Upsert = @"
        INSERT INTO progress (user_id, plugin_id, local_id, unit, value, total, completed, updated_at)
        VALUES ($user, $plugin, $local, $unit, $value, $total, $completed, $updatedAt)
        ON CONFLICT (user_id, plugin_id, local_id) DO UPDATE SET
            unit = excluded.unit,
            value = excluded.value,
            total = excluded.total,
            completed = excluded.completed,
            updated_at = excluded.updated_at;
    ";

    private const string SelectOne = $@"
        SELECT {Columns}
        FROM progress
        WHERE user_id = $user AND plugin_id = $plugin AND local_id = $local;
    ";

    // Descendants are found by walking parent links in the node cache. UNION (not UNION ALL)
    // drops repeats, so a cycle in bad data ends the walk; the depth cap bounds it regardless.
    private const string Subtree = @"
        WITH RECURSIVE subtree (local_id, depth) AS (
            SELECT $local, 0
            UNION
            SELECT child.local_id, subtree.depth + 1
            FROM nodes AS child
            JOIN subtree ON child.parent_local_id = subtree.local_id
            WHERE child.plugin_id = $plugin AND subtree.depth < 32
        )";

    private const string SelectLatestWithin = $@"
        {Subtree}
        SELECT {Columns}
        FROM progress
        WHERE user_id = $user
          AND plugin_id = $plugin
          AND local_id IN (SELECT local_id FROM subtree)
        ORDER BY updated_at DESC, local_id
        LIMIT 1;
    ";

    private const string SelectWithin = $@"
        {Subtree}
        SELECT {Columns}
        FROM progress
        WHERE user_id = $user
          AND plugin_id = $plugin
          AND local_id IN (SELECT local_id FROM subtree)
        ORDER BY updated_at DESC, local_id
        LIMIT $limit;
    ";

    private const string DeleteWithin = $@"
        {Subtree}
        DELETE FROM progress
        WHERE user_id = $user
          AND plugin_id = $plugin
          AND local_id IN (SELECT local_id FROM subtree);
    ";

    private const string DeleteAll = @"
        DELETE FROM progress WHERE user_id = $user;
    ";

    private const string DeleteOlder = @"
        DELETE FROM progress WHERE user_id = $user AND updated_at < $before;
    ";

    // A series is the topmost cached ancestor: climb parent links through the node cache from every progress entry
    // (the depth cap bounds a cycle in bad data), keep the deepest step of each climb, then group by that root. The
    // window functions give each group its counts and pick its most recently updated entry (ordinal 1).
    private const string SelectGroups = @"
        WITH RECURSIVE climb (plugin_id, start_id, current_id, depth) AS (
            SELECT plugin_id, local_id, local_id, 0
            FROM progress
            WHERE user_id = $user
            UNION ALL
            SELECT climb.plugin_id, climb.start_id, parent.local_id, climb.depth + 1
            FROM climb
            JOIN nodes AS current ON current.plugin_id = climb.plugin_id AND current.local_id = climb.current_id
            JOIN nodes AS parent ON parent.plugin_id = current.plugin_id AND parent.local_id = current.parent_local_id
            WHERE climb.depth < 32
        ),
        roots AS (
            SELECT plugin_id, start_id, current_id AS root_id, MAX(depth) AS depth
            FROM climb
            GROUP BY plugin_id, start_id
        ),
        grouped AS (
            SELECT p.user_id, p.plugin_id, p.local_id, p.unit, p.value, p.total, p.completed, p.updated_at,
                   roots.root_id,
                   ROW_NUMBER() OVER (PARTITION BY p.plugin_id, roots.root_id ORDER BY p.updated_at DESC, p.local_id) AS ordinal,
                   SUM(p.completed) OVER (PARTITION BY p.plugin_id, roots.root_id) AS completed_count,
                   COUNT(*) OVER (PARTITION BY p.plugin_id, roots.root_id) AS entry_count
            FROM progress AS p
            JOIN roots ON roots.plugin_id = p.plugin_id AND roots.start_id = p.local_id
            WHERE p.user_id = $user
        )
        SELECT user_id, plugin_id, local_id, unit, value, total, completed, updated_at,
               root_id, completed_count, entry_count
        FROM grouped
        WHERE ordinal = 1
        ORDER BY updated_at DESC, plugin_id, root_id
        LIMIT $limit;
    ";

    private const string SelectRecent = $@"
        SELECT {Columns}
        FROM progress
        WHERE user_id = $user
        ORDER BY updated_at DESC, plugin_id, local_id
        LIMIT $limit;
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public Task UpsertAsync(Progress progress, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            Upsert,
            parameters =>
            {
                parameters.Bind("$user", progress.User.Value);
                parameters.Bind("$plugin", progress.Node.Plugin.Value);
                parameters.Bind("$local", progress.Node.Id.Value);
                parameters.Bind("$unit", progress.Locator.Unit);
                parameters.Bind("$value", progress.Locator.Value);
                parameters.Bind("$total", progress.Locator.Total);
                parameters.Bind("$completed", progress.Completed ? 1 : 0);
                parameters.Bind("$updatedAt", SqliteValues.ToStored(progress.UpdatedAt));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<Progress?> GetAsync(UserId user, NodeRef node, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectOne,
            parameters => BindNode(parameters, user, node),
            ReadProgress,
            cancellationToken);

    /// <inheritdoc/>
    public Task<Progress?> GetLatestWithinAsync(UserId user, NodeRef ancestor, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectLatestWithin,
            parameters => BindNode(parameters, user, ancestor),
            ReadProgress,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Progress>> ListRecentAsync(UserId user, int limit, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            SelectRecent,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$limit", Math.Max(0, limit));
            },
            ReadProgress,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Progress>> ListWithinAsync(UserId user, NodeRef ancestor, int limit, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            SelectWithin,
            parameters =>
            {
                BindNode(parameters, user, ancestor);
                parameters.Bind("$limit", Math.Max(0, limit));
            },
            ReadProgress,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProgressGroup>> ListGroupsAsync(UserId user, int limit, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            SelectGroups,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$limit", Math.Max(0, limit));
            },
            ReadGroup,
            cancellationToken);

    /// <inheritdoc/>
    public Task<int> DeleteWithinAsync(UserId user, NodeRef root, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(DeleteWithin, parameters => BindNode(parameters, user, root), cancellationToken);

    /// <inheritdoc/>
    public Task<int> ClearAsync(UserId user, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(DeleteAll, parameters => parameters.Bind("$user", user.Value), cancellationToken);

    /// <inheritdoc/>
    public Task<int> DeleteBeforeAsync(UserId user, DateTimeOffset before, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            DeleteOlder,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$before", SqliteValues.ToStored(before));
            },
            cancellationToken);

    private static void BindNode(SqliteParameterCollection parameters, UserId user, NodeRef node)
    {
        parameters.Bind("$user", user.Value);
        parameters.Bind("$plugin", node.Plugin.Value);
        parameters.Bind("$local", node.Id.Value);
    }

    private static Progress ReadProgress(SqliteDataReader reader)
    {
        UserId user = UserId.TryCreate(reader.GetString(0), out UserId userId)
            ? userId
            : throw new InvalidDataException("A stored progress row has an invalid user id.");
        PluginId plugin = PluginId.TryCreate(reader.GetString(1), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A stored progress row has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(2), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException("A stored progress row has an invalid node id.");

        return new Progress(
            user,
            new NodeRef(plugin, local),
            new ProgressLocator(reader.GetString(3), reader.GetDouble(4), reader.GetNullableDouble(5)),
            reader.GetInt64(6) != 0,
            SqliteValues.FromStored(reader.GetInt64(7)));
    }

    private static ProgressGroup ReadGroup(SqliteDataReader reader)
    {
        Progress latest = ReadProgress(reader);
        LocalNodeId root = LocalNodeId.TryCreate(reader.GetString(8), out LocalNodeId rootId)
            ? rootId
            : throw new InvalidDataException("A stored progress row has an invalid series id.");
        int completed = reader.GetInt32(9);
        return new ProgressGroup(new NodeRef(latest.Node.Plugin, root), latest, completed, reader.GetInt32(10) - completed);
    }
}
