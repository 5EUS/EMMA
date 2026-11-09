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
        id, plugin_id, local_id, role, hint, state, items_completed, items_total, bytes_downloaded,
        error_message, created_at, updated_at, started_at, completed_at
    ";

    private const string Insert = @"
        INSERT INTO download_jobs (
            id, plugin_id, local_id, role, hint, state, items_completed, items_total, bytes_downloaded,
            error_message, created_at, updated_at, started_at, completed_at)
        VALUES (
            $id, $plugin, $local, $role, $hint, $state, $itemsCompleted, $itemsTotal, $bytes,
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

    private const string TryTransition = @"
        UPDATE download_jobs SET
            state = $state,
            items_completed = $itemsCompleted,
            items_total = $itemsTotal,
            bytes_downloaded = $bytes,
            error_message = $error,
            updated_at = $updatedAt,
            started_at = $startedAt,
            completed_at = $completedAt
        WHERE id = $id AND state = $expected;
    ";

    private const string SelectLatest = $@"
        SELECT {Columns}
        FROM download_jobs
        WHERE plugin_id = $plugin AND local_id = $local AND role = $role
        ORDER BY created_at DESC, id
        LIMIT 1;
    ";

    private const string ItemColumns = "job_id, position, plugin_id, local_id, role, file, mime, bytes, error_message";

    private const string InsertItem = @"
        INSERT INTO download_items (job_id, position, plugin_id, local_id, role, file, mime, bytes, error_message)
        VALUES ($job, $position, $plugin, $local, $role, $file, $mime, $bytes, $error);
    ";

    private const string UpdateItem = @"
        UPDATE download_items SET file = $file, mime = $mime, bytes = $bytes, error_message = $error
        WHERE job_id = $job AND position = $position;
    ";

    private const string SelectItems = $"SELECT {ItemColumns} FROM download_items WHERE job_id = $job ORDER BY position;";

    private const string SelectStored = $@"
        SELECT {ItemColumns}
        FROM download_items
        WHERE plugin_id = $plugin AND local_id = $local AND role = $role AND file IS NOT NULL
        LIMIT 1;
    ";

    private const string SelectExclusiveFiles = @"
        SELECT DISTINCT file
        FROM download_items AS mine
        WHERE job_id = $job AND file IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM download_items AS other WHERE other.file = mine.file AND other.job_id <> $job);
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
                parameters.Bind("$hint", job.Hint);
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
    public Task<DownloadJob?> FindAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectLatest,
            parameters =>
            {
                parameters.Bind("$plugin", node.Plugin.Value);
                parameters.Bind("$local", node.Id.Value);
                parameters.Bind("$role", role.Value);
            },
            ReadJob,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> TryTransitionAsync(DownloadJob job, DownloadState expected, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            TryTransition,
            parameters =>
            {
                parameters.Bind("$id", IdText(job.Id));
                parameters.Bind("$expected", (int)expected);
                BindMutable(parameters, job);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task AddItemsAsync(IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // One transaction: a job's leaves are stored whole or not at all, and one commit is one disk sync.
            SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (DownloadItem item in items)
                {
                    await SqliteDatabase.ExecuteAsync(
                        connection,
                        InsertItem,
                        parameters =>
                        {
                            parameters.Bind("$job", IdText(item.JobId));
                            parameters.Bind("$position", item.Position);
                            parameters.Bind("$plugin", item.Node.Plugin.Value);
                            parameters.Bind("$local", item.Node.Id.Value);
                            parameters.Bind("$role", item.Role.Value);
                            BindItemState(parameters, item);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DownloadItem>> ListItemsAsync(Guid jobId, CancellationToken cancellationToken) =>
        this.database.QueryAsync(SelectItems, parameters => parameters.Bind("$job", IdText(jobId)), ReadItem, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> UpdateItemAsync(DownloadItem item, CancellationToken cancellationToken) =>
        await this.database.ExecuteAsync(
            UpdateItem,
            parameters =>
            {
                parameters.Bind("$job", IdText(item.JobId));
                parameters.Bind("$position", item.Position);
                BindItemState(parameters, item);
            },
            cancellationToken).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public Task<DownloadItem?> FindStoredAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken) =>
        this.database.QuerySingleAsync(
            SelectStored,
            parameters =>
            {
                parameters.Bind("$plugin", node.Plugin.Value);
                parameters.Bind("$local", node.Id.Value);
                parameters.Bind("$role", role.Value);
            },
            ReadItem,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListExclusiveFilesAsync(Guid jobId, CancellationToken cancellationToken) =>
        this.database.QueryAsync(SelectExclusiveFiles, parameters => parameters.Bind("$job", IdText(jobId)), reader => reader.GetString(0), cancellationToken);

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

    private static void BindItemState(SqliteParameterCollection parameters, DownloadItem item)
    {
        parameters.Bind("$file", item.File);
        parameters.Bind("$mime", item.Mime);
        parameters.Bind("$bytes", item.Bytes);
        parameters.Bind("$error", item.ErrorMessage);
    }

    private static DownloadItem ReadItem(SqliteDataReader reader)
    {
        Guid job = Guid.Parse(reader.GetString(0));
        PluginId plugin = PluginId.TryCreate(reader.GetString(2), out PluginId pluginId)
            ? pluginId
            : throw new InvalidDataException($"A stored download item of job {job} has an invalid plugin id.");
        LocalNodeId local = LocalNodeId.TryCreate(reader.GetString(3), out LocalNodeId localId)
            ? localId
            : throw new InvalidDataException($"A stored download item of job {job} has an invalid node id.");
        AssetRole role = AssetRole.TryCreate(reader.GetString(4), out AssetRole assetRole)
            ? assetRole
            : throw new InvalidDataException($"A stored download item of job {job} has an invalid role.");

        return new DownloadItem(
            job,
            reader.GetInt32(1),
            new NodeRef(plugin, local),
            role,
            reader.GetNullableString(5),
            reader.GetNullableString(6),
            reader.GetInt64(7),
            reader.GetNullableString(8));
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
            reader.GetNullableString(4),
            (DownloadState)reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetNullableString(9),
            SqliteValues.FromStored(reader.GetInt64(10)),
            SqliteValues.FromStored(reader.GetInt64(11)),
            reader.GetNullableTimestamp(12),
            reader.GetNullableTimestamp(13));
    }
}
