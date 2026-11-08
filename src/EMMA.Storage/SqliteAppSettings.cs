using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>The app's remembered choices per user and scope. A change set is applied in one transaction, so it is all or none.</summary>
/// <param name="options">Where the database lives.</param>
public sealed class SqliteAppSettings(StorageOptions options) : IAppSettingsPort
{
    private const string SelectScope = @"
        SELECT setting_key, value
        FROM app_settings
        WHERE user_id = $user AND scope = $scope
        ORDER BY setting_key;
    ";

    private const string Upsert = @"
        INSERT INTO app_settings (user_id, scope, setting_key, value, updated_at)
        VALUES ($user, $scope, $key, $value, $updatedAt)
        ON CONFLICT (user_id, scope, setting_key) DO UPDATE SET
            value = excluded.value,
            updated_at = excluded.updated_at;
    ";

    private const string Delete = @"
        DELETE FROM app_settings
        WHERE user_id = $user AND scope = $scope AND setting_key = $key;
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(UserId user, string scope, CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyValuePair<string, string>> rows = await this.database.QueryAsync(
            SelectScope,
            parameters =>
            {
                parameters.Bind("$user", user.Value);
                parameters.Bind("$scope", scope);
            },
            reader => KeyValuePair.Create(reader.GetString(0), reader.GetString(1)),
            cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, string>(rows, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public async Task ApplyAsync(UserId user, string scope, IReadOnlyList<AppSettingChange> changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (AppSettingChange change in changes)
                {
                    await SqliteDatabase.ExecuteAsync(
                        connection,
                        change.Value is null ? Delete : Upsert,
                        parameters =>
                        {
                            parameters.Bind("$user", user.Value);
                            parameters.Bind("$scope", scope);
                            parameters.Bind("$key", change.Key);
                            if (change.Value is not null)
                            {
                                parameters.Bind("$value", change.Value);
                                parameters.Bind("$updatedAt", SqliteValues.ToStored(updatedAt));
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
