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
}
