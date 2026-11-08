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
    private const string SelectLatestWithin = $@"
        WITH RECURSIVE subtree (local_id, depth) AS (
            SELECT $local, 0
            UNION
            SELECT child.local_id, subtree.depth + 1
            FROM nodes AS child
            JOIN subtree ON child.parent_local_id = subtree.local_id
            WHERE child.plugin_id = $plugin AND subtree.depth < 32
        )
        SELECT {Columns}
        FROM progress
        WHERE user_id = $user
          AND plugin_id = $plugin
          AND local_id IN (SELECT local_id FROM subtree)
        ORDER BY updated_at DESC, local_id
        LIMIT 1;
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
}
