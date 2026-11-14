using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// The ordered children of nodes the host has listed completely. A listing is replaced in one transaction and its
/// state row is written last, so a reader never sees a half-written listing.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="time">The time source that stamps each listing.</param>
public sealed class SqliteNodeListingStore(StorageOptions options, TimeProvider time) : INodeListingPort
{
    private const string SelectTotal = @"
        SELECT total
        FROM node_listing_state
        WHERE plugin_id = $plugin AND parent_local_id = $parent;
    ";

    private const string SelectPage = @"
        SELECT local_id
        FROM node_listings
        WHERE plugin_id = $plugin AND parent_local_id = $parent AND position >= $offset
        ORDER BY position
        LIMIT $limit;
    ";

    private const string DeleteState = @"
        DELETE FROM node_listing_state
        WHERE plugin_id = $plugin AND parent_local_id = $parent;
    ";

    private const string InsertState = @"
        INSERT INTO node_listing_state (plugin_id, parent_local_id, total, listed_at)
        VALUES ($plugin, $parent, $total, $listedAt);
    ";

    private const string InsertChild = @"
        INSERT INTO node_listings (plugin_id, parent_local_id, position, local_id)
        VALUES ($plugin, $parent, $position, $local);
    ";

    private const string DeleteAll = "DELETE FROM node_listing_state;";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public async Task<StoredListing?> GetPageAsync(NodeRef parent, int offset, int limit, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            IReadOnlyList<long> total = await SqliteDatabase.QueryAsync(
                connection,
                SelectTotal,
                parameters => BindParent(parameters, parent),
                reader => reader.GetInt64(0),
                cancellationToken).ConfigureAwait(false);
            if (total.Count == 0)
            {
                return null;
            }

            IReadOnlyList<LocalNodeId> ids = await SqliteDatabase.QueryAsync(
                connection,
                SelectPage,
                parameters =>
                {
                    BindParent(parameters, parent);
                    parameters.Bind("$offset", offset);
                    parameters.Bind("$limit", limit);
                },
                reader => LocalNodeId.TryCreate(reader.GetString(0), out LocalNodeId id)
                    ? id
                    : throw new InvalidDataException("A stored listing has an invalid local id."),
                cancellationToken).ConfigureAwait(false);
            return new StoredListing(ids, checked((int)total[0]));
        }
    }

    /// <inheritdoc/>
    public async Task ReplaceAsync(NodeRef parent, IReadOnlyList<LocalNodeId> children, CancellationToken cancellationToken)
    {
        long now = SqliteValues.ToStored(time.GetUtcNow());
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // Deleting the state row cascades to the old children.
                await SqliteDatabase.ExecuteAsync(connection, DeleteState, parameters => BindParent(parameters, parent), cancellationToken).ConfigureAwait(false);
                await SqliteDatabase.ExecuteAsync(
                    connection,
                    InsertState,
                    parameters =>
                    {
                        BindParent(parameters, parent);
                        parameters.Bind("$total", children.Count);
                        parameters.Bind("$listedAt", now);
                    },
                    cancellationToken).ConfigureAwait(false);
                for (int position = 0; position < children.Count; position++)
                {
                    int current = position;
                    await SqliteDatabase.ExecuteAsync(
                        connection,
                        InsertChild,
                        parameters =>
                        {
                            BindParent(parameters, parent);
                            parameters.Bind("$position", current);
                            parameters.Bind("$local", children[current].Value);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken) =>
        this.database.ExecuteAsync(DeleteAll, null, cancellationToken);

    private static void BindParent(SqliteParameterCollection parameters, NodeRef parent)
    {
        parameters.Bind("$plugin", parent.Plugin.Value);
        parameters.Bind("$parent", parent.Id.Value);
    }
}
