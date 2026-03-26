namespace EMMA.Plugin.Common;

public static class PluginJsonPayload
{
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