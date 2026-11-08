using System.Globalization;
using System.Text.Json;

using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// Stores download jobs. A job is a node and a role, so one table serves every media type.
/// The state filter is passed as a single JSON parameter to <c>json_each</c>, which keeps
/// every statement static text (no placeholder lists are assembled).
/// </summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqliteDownloadStore(StorageOptions options) : IDownloadPort
{
    private const string Columns = @"
        id, plugin_id, local_id, role, state, items_completed, items_total, bytes_downloaded,
        error_message, created_at, updated_at, started_at, completed_at
    ";

    private const string Insert = @"
        INSERT INTO download_jobs (
            id, plugin_id, local_id, role, state, items_completed, items_total, bytes_downloaded,
            error_message, created_at, updated_at, started_at, completed_at)
        VALUES (
            $id, $plugin, $local, $role, $state, $itemsCompleted, $itemsTotal, $bytes,
            $error, $createdAt, $updatedAt, $startedAt, $completedAt);
    ";

    private const string Update = @"
        UPDATE download_jobs SET
            state = $state,
            items_completed = $itemsCompleted,
            items_total = $itemsTotal,
            bytes_downloaded = $bytes,
            error_message = $error,
            updated_at = $updatedAt,
            started_at = $startedAt,
            completed_at = $completedAt
        WHERE id = $id;
    ";

    private const string SelectOne = $"SELECT {Columns} FROM download_jobs WHERE id = $id;";

    private const string SelectList = $@"
        SELECT {Columns}
        FROM download_jobs
        WHERE $states IS NULL OR state IN (SELECT value FROM json_each($states))
        ORDER BY updated_at DESC, id
        LIMIT $limit;
    ";

    private const string Delete = "DELETE FROM download_jobs WHERE id = $id;";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public Task CreateAsync(DownloadJob job, CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(
            Insert,
            parameters =>
            {
                parameters.Bind("$id", IdText(job.Id));
                parameters.Bind("$plugin", job.Node.Plugin.Value);
                parameters.Bind("$local", job.Node.Id.Value);
                parameters.Bind("$role", job.Role.Value);
                BindMutable(parameters, job);
                parameters.Bind("$createdAt", SqliteValues.ToStored(job.CreatedAt));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<DownloadJob?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectOne,
            parameters => parameters.Bind("$id", IdText(id)),
            ReadJob,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DownloadJob>> ListAsync(
        IReadOnlyCollection<DownloadState>? states,
        int limit,
        CancellationToken cancellationToken)
    {
        string? statesJson = states is null
            ? null
            : JsonSerializer.Serialize([.. states.Select(state => (int)state)], StorageJsonContext.Default.Int32Array);

        return this.database.QueryAsync(
            SelectList,
            parameters =>
            {
                parameters.Bind("$states", statesJson);
                parameters.Bind("$limit", Math.Max(0, limit));
            },
            ReadJob,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(DownloadJob job, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Update,
            parameters =>
            {
                parameters.Bind("$id", IdText(job.Id));
                BindMutable(parameters, job);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            Delete,
            parameters => parameters.Bind("$id", IdText(id)),
            cancellationToken).ConfigureAwait(false) > 0;

    private static string IdText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static void BindMutable(SqliteParameterCollection parameters, DownloadJob job)
    {
        parameters.Bind("$state", (int)job.State);
        parameters.Bind("$itemsCompleted", job.ItemsCompleted);
        parameters.Bind("$itemsTotal", job.ItemsTotal);
        parameters.Bind("$bytes", job.BytesDownloaded);
        parameters.Bind("$error", job.ErrorMessage);
        parameters.Bind("$updatedAt", SqliteValues.ToStored(job.UpdatedAt));
        parameters.Bind("$startedAt", SqliteValues.ToStored(job.StartedAt));
        parameters.Bind("$completedAt", SqliteValues.ToStored(job.CompletedAt));
    }

    private static DownloadJob ReadJob(SqliteDataReader reader)
    {
        Guid id = Guid.Parse(reader.GetString(0));
        PluginId plugin = PluginId.TryCreate(reader.GetString(1), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException($"The stored download job {id} has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(2), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException($"The stored download job {id} has an invalid node id.");
        AssetRole role = AssetRole.TryCreate(reader.GetString(3), out AssetRole assetRole)
            ? assetRole
            : throw new InvalidDataException($"The stored download job {id} has an invalid role.");

        return new DownloadJob(
            id,
            new NodeRef(plugin, local),
            role,
            (DownloadState)reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt64(7),
            reader.GetNullableString(8),
            SqliteValues.FromStored(reader.GetInt64(9)),
            SqliteValues.FromStored(reader.GetInt64(10)),
            reader.GetNullableTimestamp(11),
            reader.GetNullableTimestamp(12));
    }
}
