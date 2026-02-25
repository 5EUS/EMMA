using Microsoft.Data.Sqlite;

namespace EMMA.Storage;

internal sealed class SqliteConnectionFactory(StorageOptions options)
{
    private readonly StorageOptions _options = options;

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            ForeignKeys = true
        };

        return new SqliteConnection(builder.ConnectionString);
    }
}
