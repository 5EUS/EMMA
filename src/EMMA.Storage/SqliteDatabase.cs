using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

/// <summary>
/// The one place that opens connections and runs commands, so every store is just SQL
/// plus row mapping. All SQL is static text with named parameters; nothing is ever
/// concatenated into a statement.
/// </summary>
/// <param name="options">Where the database lives.</param>
internal sealed class SqliteDatabase(StorageOptions options)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
    }.ConnectionString;

    /// <summary>Runs a statement on an existing connection or transaction scope.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The SQL text.</param>
    /// <param name="bind">Binds the parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of rows changed.</returns>
    public static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        Action<SqliteParameterCollection>? bind,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = CreateCommand(connection, sql, bind);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a query on an existing connection and maps every row.</summary>
    /// <typeparam name="T">The mapped row type.</typeparam>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The SQL text.</param>
    /// <param name="bind">Binds the parameters, or <see langword="null"/>.</param>
    /// <param name="map">Maps the current row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The mapped rows, in query order.</returns>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        SqliteConnection connection,
        string sql,
        Action<SqliteParameterCollection>? bind,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = CreateCommand(connection, sql, bind);
        await using (command.ConfigureAwait(false))
        {
            SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                List<T> rows = [];
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
        }
    }

    /// <summary>Opens a connection. The caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>An open connection.</returns>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(this.connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="bind">Binds the parameters, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of rows changed.</returns>
    public async Task<int> ExecuteAsync(string sql, Action<SqliteParameterCollection>? bind, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await ExecuteAsync(connection, sql, bind, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a query and maps every row.</summary>
    /// <typeparam name="T">The mapped row type.</typeparam>
    /// <param name="sql">The SQL text.</param>
    /// <param name="bind">Binds the parameters, or <see langword="null"/>.</param>
    /// <param name="map">Maps the current row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The mapped rows, in query order.</returns>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Action<SqliteParameterCollection>? bind,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await QueryAsync(connection, sql, bind, map, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a query expected to return at most one row.</summary>
    /// <typeparam name="T">The mapped row type.</typeparam>
    /// <param name="sql">The SQL text.</param>
    /// <param name="bind">Binds the parameters, or <see langword="null"/>.</param>
    /// <param name="map">Maps the row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The mapped row, or <see langword="null"/> when there is none.</returns>
    public async Task<T?> QuerySingleAsync<T>(
        string sql,
        Action<SqliteParameterCollection>? bind,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        IReadOnlyList<T> rows = await this.QueryAsync(sql, bind, map, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, Action<SqliteParameterCollection>? bind)
    {
        SqliteCommand command = connection.CreateCommand();

        // Every caller passes a compile-time constant (see AGENTS.md); user input only ever
        // reaches the database through the bound parameters, never through this text.
#pragma warning disable CA2100 // The analyzer cannot see that callers pass constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        bind?.Invoke(command.Parameters);
        return command;
    }
}
