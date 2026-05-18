namespace EMMA.Plugin.Common;

/// <summary>
/// Normalizes payload strings into plain JSON content.
/// </summary>
public static class PluginJsonPayload
{
    /// <summary>
    /// Normalizes a payload string by trimming it and stripping any non-JSON prefix before the first object or array token.
    /// </summary>
    /// <param name="payload">The payload text to normalize.</param>
    /// <returns>The normalized JSON payload, or an empty string when no JSON content can be found.</returns>
    public static string Normalize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        var objectIndex = trimmed.IndexOf('{');
        var arrayIndex = trimmed.IndexOf('[');

        var start = -1;
        if (objectIndex >= 0 && arrayIndex >= 0)
        {
            start = Math.Min(objectIndex, arrayIndex);
        }
        else if (objectIndex >= 0)
        {
            start = objectIndex;
        }
        else if (arrayIndex >= 0)
        {
            start = arrayIndex;
        }

        return start >= 0 ? trimmed[start..] : string.Empty;
    }
}