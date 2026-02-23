namespace EMMA.Storage;

/// <summary>
/// Storage path helpers for temp assets.
/// </summary>
public static class StoragePaths
{
    public static string GetTempAssetPath(string root, string key)
    {
        var safeKey = key.Replace("/", "-").Replace("\\", "-");
        var date = DateTime.UtcNow;
        var subdir = Path.Combine(date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
        return Path.Combine(root, subdir, safeKey);
    }
}
