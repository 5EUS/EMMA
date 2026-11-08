using EMMA.Application.Ports;
using EMMA.Domain;

using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// The user's settings for each plugin. Secrets are encrypted by an <see cref="ISecretBox"/> before they reach the
/// database, with the plugin and key as context, so a copied database reveals none and a stored secret cannot be
/// moved to another setting. A secret the box can no longer open (its key file is gone) reads as not set, so the
/// plugin still starts and the user is asked for the value again.
/// </summary>
/// <param name="options">Where the database lives.</param>
/// <param name="secrets">Encrypts and decrypts secrets.</param>
/// <param name="time">Stamps each change.</param>
public sealed class SqlitePluginSettings(StorageOptions options, ISecretBox secrets, TimeProvider time) : IPluginSettingsPort
{
    private const string SelectAll = @"
        SELECT setting_key, secret, value
        FROM plugin_settings
        WHERE plugin_id = $plugin
        ORDER BY setting_key, position;
    ";

    private const string DeleteKey = @"
        DELETE FROM plugin_settings
        WHERE plugin_id = $plugin AND setting_key = $key;
    ";

    private const string Insert = @"
        INSERT INTO plugin_settings (plugin_id, setting_key, position, value, secret, updated_at)
        VALUES ($plugin, $key, $position, $value, $secret, $updatedAt);
    ";

    private readonly SqliteDatabase database = new(options);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        IReadOnlyList<Row> rows = await this.ReadAsync(plugin, cancellationToken).ConfigureAwait(false);
        Dictionary<string, IReadOnlyList<string>> values = new(StringComparer.Ordinal);
        foreach (IGrouping<string, Row> group in rows.GroupBy(row => row.Key, StringComparer.Ordinal))
        {
            List<string> opened = [];
            foreach (Row row in group)
            {
                if (!row.IsSecret)
                {
                    opened.Add(row.Value);
                }
                else if (await secrets.UnprotectAsync(row.Value, Context(plugin, row.Key), cancellationToken).ConfigureAwait(false) is { } secret)
                {
                    opened.Add(secret);
                }
            }

            if (opened.Count > 0)
            {
                values[group.Key] = opened;
            }
        }

        return values;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredSetting>> ListAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        IReadOnlyList<Row> rows = await this.ReadAsync(plugin, cancellationToken).ConfigureAwait(false);
        List<StoredSetting> settings = [];
        foreach (IGrouping<string, Row> group in rows.GroupBy(row => row.Key, StringComparer.Ordinal))
        {
            Row first = group.First();
            if (first.IsSecret)
            {
                // Listed as set only if it can still be opened: an unreadable secret is as good as missing.
                if (await secrets.UnprotectAsync(first.Value, Context(plugin, group.Key), cancellationToken).ConfigureAwait(false) is not null)
                {
                    settings.Add(new StoredSetting(group.Key, IsSecret: true, []));
                }
            }
            else
            {
                settings.Add(new StoredSetting(group.Key, IsSecret: false, [.. group.Select(row => row.Value)]));
            }
        }

        return settings;
    }

    /// <inheritdoc/>
    public async Task ApplyAsync(PluginId plugin, IReadOnlyList<SettingWrite> writes, CancellationToken cancellationToken)
    {
        long now = SqliteValues.ToStored(time.GetUtcNow());

        // Encrypt before the transaction opens, so the lock is held only for the writes.
        IReadOnlyList<Prepared> prepared = await this.PrepareAsync(plugin, writes, cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // One transaction: a request that changes several settings is applied whole or not at all.
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (Prepared write in prepared)
                {
                    await ReplaceAsync(connection, plugin, write, now, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReplaceAsync(SqliteConnection connection, PluginId plugin, Prepared write, long now, CancellationToken cancellationToken)
    {
        await SqliteDatabase.ExecuteAsync(
            connection,
            DeleteKey,
            parameters =>
            {
                parameters.Bind("$plugin", plugin.Value);
                parameters.Bind("$key", write.Key);
            },
            cancellationToken).ConfigureAwait(false);

        for (int position = 0; position < write.Values.Length; position++)
        {
            int at = position;
            await SqliteDatabase.ExecuteAsync(
                connection,
                Insert,
                parameters =>
                {
                    parameters.Bind("$plugin", plugin.Value);
                    parameters.Bind("$key", write.Key);
                    parameters.Bind("$position", at);
                    parameters.Bind("$value", write.Values[at]);
                    parameters.Bind("$secret", write.IsSecret ? 1 : 0);
                    parameters.Bind("$updatedAt", now);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    // The context binds a ciphertext to its row. The plugin id has no newline, so the join is unambiguous.
    private static string Context(PluginId plugin, string key) => $"{plugin.Value}\n{key}";

    private async Task<IReadOnlyList<Prepared>> PrepareAsync(PluginId plugin, IReadOnlyList<SettingWrite> writes, CancellationToken cancellationToken)
    {
        List<Prepared> prepared = [];
        foreach (SettingWrite write in writes)
        {
            string[] values = new string[write.Values.Count];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = write.IsSecret
                    ? await secrets.ProtectAsync(write.Values[index], Context(plugin, write.Key), cancellationToken).ConfigureAwait(false)
                    : write.Values[index];
            }

            prepared.Add(new Prepared(write.Key, write.IsSecret, values));
        }

        return prepared;
    }

    private Task<IReadOnlyList<Row>> ReadAsync(PluginId plugin, CancellationToken cancellationToken) =>
        this.database.QueryAsync(
            SelectAll,
            parameters => parameters.Bind("$plugin", plugin.Value),
            reader => new Row(reader.GetString(0), reader.GetInt64(1) == 1, reader.GetString(2)),
            cancellationToken);

    private sealed record Row(string Key, bool IsSecret, string Value);

    private sealed record Prepared(string Key, bool IsSecret, string[] Values);
}
