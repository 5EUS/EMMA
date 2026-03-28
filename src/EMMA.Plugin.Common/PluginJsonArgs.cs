using System.Text.Json;

namespace EMMA.Plugin.Common;

public static class PluginJsonArgs
{
    public static string GetString(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    public static uint? GetUInt32(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt32(out var numeric))
            {
                return numeric;
            }

            if (prop.ValueKind == JsonValueKind.String
                && uint.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    public static int? GetInt32(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            if (prop.ValueKind == JsonValueKind.String
                && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    public static bool? GetBool(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return prop.GetBoolean();
            }

            if (prop.ValueKind == JsonValueKind.String
                && bool.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    public static IReadOnlyList<string> GetStringArray(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop)
                || prop.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. prop.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())];
        }
        catch
        {
            return [];
        }
    }
}
