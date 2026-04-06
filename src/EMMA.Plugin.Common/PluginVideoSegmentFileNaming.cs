namespace EMMA.Plugin.Common;

public static class PluginVideoSegmentFileNaming
{
    public static string ResolveSegmentExtension(Uri segmentUri, string fallback = ".ts")
    {
        ArgumentNullException.ThrowIfNull(segmentUri);

        var extension = Path.GetExtension(segmentUri.AbsolutePath ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return NormalizeSupportedSegmentExtension(fallback);
        }

        return NormalizeSupportedSegmentExtension(extension);
    }

    public static string NormalizeSupportedSegmentExtension(string extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ".ts";
        }

        if (!normalized.StartsWith('.'))
        {
            normalized = $".{normalized}";
        }

        return normalized switch
        {
            ".m4s" => ".m4s",
            ".ts" => ".ts",
            ".mp4" => ".mp4",
            ".aac" => ".aac",
            ".mp3" => ".mp3",
            ".bin" => ".bin",
            _ => ".ts"
        };
    }
}