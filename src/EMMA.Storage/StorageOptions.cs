namespace EMMA.Storage;

/// <summary>
/// Storage configuration for SQLite persistence.
/// </summary>
public sealed record StorageOptions(string DatabasePath)
{
    public string TempAssetRootDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "emma-storage", "temp-assets");

    public int TempAssetRetentionDays { get; init; } = 7;

    public static StorageOptions Default { get; } = new(ResolveDefaultDatabasePath());

    private static string ResolveDefaultDatabasePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("EMMA_STORAGE_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "EMMA", "emma.db");
        }

        return Path.Combine(Path.GetTempPath(), "EMMA", "emma.db");
    }
}
