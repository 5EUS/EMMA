using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// A user's saved nodes in named libraries. Entries hold only a node reference, never a copy of the node, so
/// clearing the node cache cannot lose anyone's library. Library names are a NOCASE column, so every comparison
/// here ignores case and the stored casing is the one the library was created with.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="time">Stamps the default library when it is first created.</param>
public sealed class SqliteLibraryStore(StorageOptions options, TimeProvider time) : ILibraryPort
{
    private const string EntryColumns = "l.user_id, libs.name, l.plugin_id, l.local_id, l.added_at";

    // The default library is created on demand, so the foreign key below always has its parent.
    private const string EnsureDefault = @"
        INSERT INTO libraries (user_id, name, created_at)
        VALUES ($user, $name, $createdAt)
        ON CONFLICT (user_id, name) DO NOTHING;
    ";

    // DO NOTHING keeps the original library and date when a node is added twice. A missing library inserts nothing,
    // through the join, rather than failing the foreign key.
    private const string Add = @"
        INSERT INTO library (user_id, plugin_id, local_id, library_name, added_at)
        SELECT $user, $plugin, $local, libs.name, $addedAt
        FROM libraries AS libs
        WHERE libs.user_id = $user AND libs.name = $library
        ON CONFLICT (user_id, plugin_id, local_id) DO NOTHING;
    ";

    private const string Remove = @"
        DELETE FROM library
        WHERE user_id = $user AND plugin_id = $plugin AND local_id = $local;
    ";

    // $library is NULL for every library.
    private const string List = $@"
        SELECT {EntryColumns}
        FROM library AS l
        JOIN libraries AS libs ON libs.user_id = l.user_id AND libs.name = l.library_name
        WHERE l.user_id = $user AND ($library IS NULL OR l.library_name = $library)
        ORDER BY l.added_at DESC, l.plugin_id, l.local_id;
    ";

    // The title is matched with instr(lower(..)): no wildcard characters to escape, so the text is only ever a value.
    private const string Search = $@"
        SELECT {EntryColumns}
        FROM library AS l
        JOIN libraries AS libs ON libs.user_id = l.user_id AND libs.name = l.library_name
        JOIN nodes AS n ON n.plugin_id = l.plugin_id AND n.local_id = l.local_id
        WHERE l.user_id = $user
          AND ($library IS NULL OR l.library_name = $library)
          AND instr(lower(n.title), lower($text)) > 0
        ORDER BY l.added_at DESC, l.plugin_id, l.local_id
        LIMIT $limit;
    ";

    private const string ListLibraries = @"
        SELECT libs.name, libs.created_at, COUNT(l.local_id)
        FROM libraries AS libs
        LEFT JOIN library AS l ON l.user_id = libs.user_id AND l.library_name = libs.name
        WHERE libs.user_id = $user
        GROUP BY libs.name, libs.created_at
        ORDER BY (libs.name = $default) DESC, libs.name;
    ";

    private const string CreateLibrary = EnsureDefault;

    private const string DeleteLibrary = @"
        DELETE FROM libraries
        WHERE user_id = $user AND name = $name;
    ";

    private const string Move = @"
        UPDATE library
        SET library_name = (SELECT libs.name FROM libraries AS libs WHERE libs.user_id = $user AND libs.name = $target)
        WHERE user_id = $user AND plugin_id = $plugin AND local_id = $local
          AND EXISTS (SELECT 1 FROM libraries AS libs WHERE libs.user_id = $user AND libs.name = $target);
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public async Task<bool> AddAsync(LibraryEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Library.Matches(LibraryName.Default))
        {
            await this.EnsureDefaultLibraryAsync(entry.User, cancellationToken).ConfigureAwait(false);
        }

        return await this.database.ExecuteAsync(
            Add,
            parameters =>
            {
                parameters.Bind("$user", entry.User.Value);
                parameters.Bind("$plugin", entry.Node.Plugin.Value);
                parameters.Bind("$local", entry.Node.Id.Value);
                parameters.Bind("$library", entry.Library.Value);
                parameters.Bind("$addedAt", SqliteValues.ToStored(entry.AddedAt));
            },
            cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(UserId user, NodeRef node, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Remove,
            parameters => BindNode(parameters, user, node),
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryEntry>> ListAsync(UserId user, LibraryName? library, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            List,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$library", library?.Value);
            },
            ReadEntry,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryEntry>> SearchAsync(UserId user, string text, LibraryName? library, int limit, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            Search,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$library", library?.Value);
                parameters.Bind("$text", text);
                parameters.Bind("$limit", Math.Max(0, limit));
            },
            ReadEntry,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibrarySummary>> ListLibrariesAsync(UserId user, CancellationToken cancellationToken)
    {
        await this.EnsureDefaultLibraryAsync(user, cancellationToken).ConfigureAwait(false);
        return await this.database.QueryAsync(
            ListLibraries,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$default", LibraryName.Default.Value);
            },
            reader => new LibrarySummary(
                LibraryName.TryCreate(reader.GetString(0), out LibraryName name)
                    ? name
                    : throw new InvalidDataException("A stored library has an invalid name."),
                SqliteValues.FromStored(reader.GetInt64(1)),
                reader.GetInt32(2)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> CreateLibraryAsync(UserId user, LibraryName name, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            CreateLibrary,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$name", name.Value);
                parameters.Bind("$createdAt", SqliteValues.ToStored(createdAt));
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<bool> DeleteLibraryAsync(UserId user, LibraryName name, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            DeleteLibrary,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$name", name.Value);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<bool> MoveAsync(UserId user, NodeRef node, LibraryName target, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Move,
            parameters =>
            {
                BindNode(parameters, user, node);
                parameters.Bind("$target", target.Value);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    private static void BindNode(SqliteParameterCollection parameters, UserId user, NodeRef node)
    {
        parameters.Bind("$user", user.Value);
        parameters.Bind("$plugin", node.Plugin.Value);
        parameters.Bind("$local", node.Id.Value);
    }

    private static LibraryEntry ReadEntry(SqliteDataReader reader)
    {
        UserId user = UserId.TryCreate(reader.GetString(0), out UserId userId)
            ? userId
            : throw new InvalidDataException("A stored library entry has an invalid user id.");
        LibraryName library = LibraryName.TryCreate(reader.GetString(1), out LibraryName name)
            ? name
            : throw new InvalidDataException("A stored library entry has an invalid library name.");
        PluginId plugin = PluginId.TryCreate(reader.GetString(2), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A stored library entry has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(3), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException("A stored library entry has an invalid node id.");

        return new LibraryEntry(user, library, new NodeRef(plugin, local), SqliteValues.FromStored(reader.GetInt64(4)));
    }

    private Task<int> EnsureDefaultLibraryAsync(UserId user, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            EnsureDefault,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$name", LibraryName.Default.Value);
                parameters.Bind("$createdAt", SqliteValues.ToStored(time.GetUtcNow()));
            },
            cancellationToken);
}
