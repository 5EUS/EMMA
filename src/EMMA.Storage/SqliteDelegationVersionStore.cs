using EMMA.Application.Ports;

namespace EMMA.Storage;

/// <summary>
/// Stores the highest accepted delegation-file version per repository. The comparison is
/// part of the write (<c>WHERE excluded.version &gt;= version</c>), so two processes racing
/// cannot let an older version overwrite a newer one.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="time">The time source that stamps each write.</param>
public sealed class SqliteDelegationVersionStore(StorageOptions options, TimeProvider time) : IDelegationVersionPort
{
    private const string Advance = @"
        INSERT INTO trusted_delegation_versions (repository_id, version, updated_at)
        VALUES ($repository, $version, $updatedAt)
        ON CONFLICT (repository_id) DO UPDATE SET
            version = excluded.version,
            updated_at = excluded.updated_at
        WHERE excluded.version >= trusted_delegation_versions.version;
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public async Task<bool> TryAdvanceAsync(string repositoryId, long version, CancellationToken cancellationToken) =>
        version >= 0
        && await this.database.ExecuteAsync(
            Advance,
            parameters =>
            {
                parameters.Bind("$repository", repositoryId);
                parameters.Bind("$version", version);
                parameters.Bind("$updatedAt", SqliteValues.ToStored(time.GetUtcNow()));
            },
            cancellationToken).ConfigureAwait(false) > 0;
}
