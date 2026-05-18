using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Reads typed values from operation argument JSON payloads.
/// </summary>
public static class PluginJsonArgs
{
    /// <summary>
    /// Reads a string property from an argument JSON object.
    /// </summary>
    /// <param name="argsJson">The JSON object that contains operation arguments.</param>
    /// <param name="key">The property name to read.</param>
    /// <returns>The string value when present and valid; otherwise, an empty string.</returns>
    public static string GetString(string? argsJson, string key)
    {
        return ReadProperty(
            argsJson,
            key,
            prop => prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? string.Empty
                : string.Empty,
            string.Empty);
    }

    /// <summary>
    /// Reads an unsigned 32-bit integer property from an argument JSON object.
    /// </summary>
    /// <param name="argsJson">The JSON object that contains operation arguments.</param>
    /// <param name="key">The property name to read.</param>
    /// <returns>The parsed unsigned integer when present and valid; otherwise, <see langword="null"/>.</returns>
    public static uint? GetUInt32(string? argsJson, string key)
    {
        return ReadProperty<uint?>(
            argsJson,
            key,
            prop =>
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt32(out var numeric))
                {
                    return numeric;
                }

                if (prop.ValueKind == JsonValueKind.String
                    && uint.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }

                return null;
            },
            null);
    }

    /// <summary>
    /// Reads a signed 32-bit integer property from an argument JSON object.
    /// </summary>
    /// <param name="argsJson">The JSON object that contains operation arguments.</param>
    /// <param name="key">The property name to read.</param>
    /// <returns>The parsed integer when present and valid; otherwise, <see langword="null"/>.</returns>
    public static int? GetInt32(string? argsJson, string key)
    {
        return ReadProperty<int?>(
            argsJson,
            key,
            prop =>
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var numeric))
                {
                    return numeric;
                }

                if (prop.ValueKind == JsonValueKind.String
                    && int.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }

                return null;
            },
            null);
    }

    /// <summary>
    /// Reads a Boolean property from an argument JSON object.
    /// </summary>
    /// <param name="argsJson">The JSON object that contains operation arguments.</param>
    /// <param name="key">The property name to read.</param>
    /// <returns>The parsed Boolean when present and valid; otherwise, <see langword="null"/>.</returns>
    public static bool? GetBool(string? argsJson, string key)
    {
        return ReadProperty<bool?>(
            argsJson,
            key,
            prop =>
            {
                if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return prop.GetBoolean();
                }

                if (prop.ValueKind == JsonValueKind.String
                    && bool.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }

                return null;
            },
            null);
    }

    /// <summary>
    /// Reads a string array property from an argument JSON object.
    /// </summary>
    /// <param name="argsJson">The JSON object that contains operation arguments.</param>
    /// <param name="key">The property name to read.</param>
    /// <returns>The trimmed string values found in the array, or an empty list when the property is missing or invalid.</returns>
    public static IReadOnlyList<string> GetStringArray(string? argsJson, string key)
    {
        return ReadProperty<IReadOnlyList<string>>(
            argsJson,
            key,
            prop =>
            {
                if (prop.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return [.. prop.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())];
            },
            []);
    }

    private static T ReadProperty<T>(
        string? argsJson,
        string key,
        Func<JsonElement, T> map,
        T fallback)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return fallback;
            }

            return map(prop);
        }
        catch
        {
            return fallback;
        }
    }
}
