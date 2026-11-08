using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>The SQLite record of installed plugins.</summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqlitePluginRegistry(StorageOptions options) : IPluginRegistryPort
{
    private const string Columns = "id, version, status, manifest_json, signer, installed_at, updated_at";

    private const string Upsert = $@"
        INSERT INTO plugins ({Columns})
        VALUES ($id, $version, $status, $manifest, $signer, $installedAt, $updatedAt)
        ON CONFLICT (id) DO UPDATE SET
            version = excluded.version,
            status = excluded.status,
            manifest_json = excluded.manifest_json,
            signer = excluded.signer,
            installed_at = excluded.installed_at,
            updated_at = excluded.updated_at;
    ";

    private const string SelectOne = $"SELECT {Columns} FROM plugins WHERE id = $id;";

    private const string SelectAll = $"SELECT {Columns} FROM plugins ORDER BY id;";

    private const string SetStatus = "UPDATE plugins SET status = $status, updated_at = $updatedAt WHERE id = $id;";

    private const string Remove = "DELETE FROM plugins WHERE id = $id;";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public Task UpsertAsync(InstalledPlugin plugin, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            Upsert,
            parameters =>
            {
                parameters.Bind("$id", plugin.Id.Value);
                parameters.Bind("$version", plugin.Version);
                parameters.Bind("$status", (int)plugin.Status);
                parameters.Bind("$manifest", plugin.ManifestJson);
                parameters.Bind("$signer", plugin.Signer);
                parameters.Bind("$installedAt", SqliteValues.ToStored(plugin.InstalledAt));
                parameters.Bind("$updatedAt", SqliteValues.ToStored(plugin.UpdatedAt));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<InstalledPlugin?> GetAsync(PluginId id, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectOne,
            parameters => parameters.Bind("$id", id.Value),
            ReadPlugin,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<InstalledPlugin>> ListAsync(CancellationToken cancellationToken) =>
        this.database.QueryAsync(SelectAll, null, ReadPlugin, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> SetStatusAsync(PluginId id, PluginStatus status, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            SetStatus,
            parameters =>
            {
                parameters.Bind("$id", id.Value);
                parameters.Bind("$status", (int)status);
                parameters.Bind("$updatedAt", SqliteValues.ToStored(updatedAt));
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(PluginId id, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Remove,
            parameters => parameters.Bind("$id", id.Value),
            cancellationToken).ConfigureAwait(false) > 0;

    private static InstalledPlugin ReadPlugin(SqliteDataReader reader)
    {
        PluginId id = PluginId.TryCreate(reader.GetString(0), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException("A stored plugin has an invalid id.");

        return new InstalledPlugin(
            id,
            reader.GetString(1),
            (PluginStatus)reader.GetInt32(2),
            reader.GetString(3),
            reader.GetNullableString(4),
            SqliteValues.FromStored(reader.GetInt64(5)),
            SqliteValues.FromStored(reader.GetInt64(6)));
    }
}
