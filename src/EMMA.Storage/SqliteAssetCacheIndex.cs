using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>Remembers which files the disk asset cache holds, so the cache survives a restart.</summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqliteAssetCacheIndex(StorageOptions options) : IAssetCacheIndexPort
{
    private const string SelectAll = @"
        SELECT cache_key, plugin_id, local_id, file, mime, bytes, fetched_at, last_used_at
        FROM asset_cache;
    ";

    private const string Upsert = @"
        INSERT INTO asset_cache (cache_key, plugin_id, local_id, file, mime, bytes, fetched_at, last_used_at)
        VALUES ($key, $plugin, $local, $file, $mime, $bytes, $fetchedAt, $lastUsedAt)
        ON CONFLICT (cache_key) DO UPDATE SET
            plugin_id = excluded.plugin_id,
            local_id = excluded.local_id,
            file = excluded.file,
            mime = excluded.mime,
            bytes = excluded.bytes,
            fetched_at = excluded.fetched_at,
            last_used_at = excluded.last_used_at;
    ";

    private const string Touch = "UPDATE asset_cache SET last_used_at = $lastUsedAt WHERE cache_key = $key;";

    private const string Delete = "DELETE FROM asset_cache WHERE cache_key = $key;";

    private const string DeleteAll = "DELETE FROM asset_cache;";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public Task<IReadOnlyList<AssetCacheRecord>> LoadAsync(CancellationToken cancellationToken) =>
        this.database.QueryAsync(SelectAll, null, ReadRecord, cancellationToken);

    /// <inheritdoc/>
    public Task UpsertAsync(AssetCacheRecord record, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            Upsert,
            parameters =>
            {
                parameters.Bind("$key", record.Key);
                parameters.Bind("$plugin", record.Node.Plugin.Value);
                parameters.Bind("$local", record.Node.Id.Value);
                parameters.Bind("$file", record.File);
                parameters.Bind("$mime", record.Mime);
                parameters.Bind("$bytes", record.Bytes);
                parameters.Bind("$fetchedAt", SqliteValues.ToStored(record.FetchedAt));
                parameters.Bind("$lastUsedAt", SqliteValues.ToStored(record.LastUsedAt));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task TouchAsync(IReadOnlyDictionary<string, DateTimeOffset> lastUsed, CancellationToken cancellationToken) =>
        this.InTransactionAsync(
            lastUsed,
            Touch,
            (parameters, entry) =>
            {
                parameters.Bind("$key", entry.Key);
                parameters.Bind("$lastUsedAt", SqliteValues.ToStored(entry.Value));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        this.InTransactionAsync(keys, Delete, (parameters, key) => parameters.Bind("$key", key), cancellationToken);

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(DeleteAll, null, cancellationToken);

    private static AssetCacheRecord ReadRecord(SqliteDataReader reader)
    {
        PluginId plugin = PluginId.TryCreate(reader.GetString(1), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A cached asset has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(2), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException("A cached asset has an invalid local id.");
        return new AssetCacheRecord(
            reader.GetString(0),
            new NodeRef(plugin, local),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            SqliteValues.FromStored(reader.GetInt64(6)),
            SqliteValues.FromStored(reader.GetInt64(7)));
    }

    private async Task InTransactionAsync<T>(IEnumerable<T> items, string sql, Action<SqliteParameterCollection, T> bind, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (T item in items)
                {
                    await SqliteDatabase.ExecuteAsync(connection, sql, parameters => bind(parameters, item), cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
