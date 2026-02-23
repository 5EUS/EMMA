namespace EMMA.Storage;

/// <summary>
/// Storage configuration for SQLite persistence.
/// </summary>
public sealed record StorageOptions(string DatabasePath)
{
    public string TempAssetRootDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "emma-storage", "temp-assets");

    public int TempAssetRetentionDays { get; init; } = 7;

    public static StorageOptions Default { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "emma.db"));
}
