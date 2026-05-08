namespace EMMA.Plugin.Common;

/// <summary>
/// Normalizes file extensions used for downloaded video segments.
/// </summary>
public static class PluginVideoSegmentFileNaming
{
    /// <summary>
    /// Resolves a supported file extension for a video segment URI.
    /// </summary>
    /// <param name="segmentUri">The segment URI to inspect.</param>
    /// <param name="fallback">The fallback extension to use when the URI does not include one.</param>
    /// <returns>A normalized supported file extension.</returns>
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

    /// <summary>
    /// Normalizes a segment file extension to the supported extension set.
    /// </summary>
    /// <param name="extension">The extension to normalize.</param>
    /// <returns>A supported normalized extension, defaulting to <c>.ts</c> when the input is unsupported.</returns>
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