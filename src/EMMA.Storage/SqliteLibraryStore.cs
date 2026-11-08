using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// A user's saved nodes. Entries hold only a node reference, never a copy of the node, so
/// clearing the node cache cannot lose anyone's library.
/// </summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqliteLibraryStore(StorageOptions options) : ILibraryPort
{
    // DO NOTHING keeps the original date when a node is added twice.
    private const string Add = @"
        INSERT INTO library (user_id, plugin_id, local_id, added_at)
        VALUES ($user, $plugin, $local, $addedAt)
        ON CONFLICT (user_id, plugin_id, local_id) DO NOTHING;
    ";

    private const string Remove = @"
        DELETE FROM library
        WHERE user_id = $user AND plugin_id = $plugin AND local_id = $local;
    ";

    private const string List = @"
        SELECT user_id, plugin_id, local_id, added_at
        FROM library
        WHERE user_id = $user
        ORDER BY added_at DESC, plugin_id, local_id;
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public Task AddAsync(LibraryEntry entry, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            Add,
            parameters =>
            {
                parameters.Bind("$user", entry.User.Value);
                parameters.Bind("$plugin", entry.Node.Plugin.Value);
                parameters.Bind("$local", entry.Node.Id.Value);
                parameters.Bind("$addedAt", SqliteValues.ToStored(entry.AddedAt));
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(UserId user, NodeRef node, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Remove,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$plugin", node.Plugin.Value);
                parameters.Bind("$local", node.Id.Value);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryEntry>> ListAsync(UserId user, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            List,
            parameters => parameters.Bind("$user", user.Value),
            ReadEntry,
            cancellationToken);

    private static LibraryEntry ReadEntry(SqliteDataReader reader)
    {
        UserId user = UserId.TryCreate(reader.GetString(0), out UserId userId)
            ? userId
            : throw new InvalidDataException("A stored library entry has an invalid user id.");
        PluginId plugin = PluginId.TryCreate(reader.GetString(1), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A stored library entry has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(2), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException("A stored library entry has an invalid node id.");

        return new LibraryEntry(user, new NodeRef(plugin, local), SqliteValues.FromStored(reader.GetInt64(3)));
    }
}
