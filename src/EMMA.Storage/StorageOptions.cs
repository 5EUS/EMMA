namespace EMMA.Storage;

/// <summary>
/// Storage configuration for SQLite persistence.
/// </summary>
public sealed record StorageOptions(string DatabasePath)
{
    public static StorageOptions Default { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "emma.db"));
}
