using System.Text.Json;

namespace EMMA.Plugin.Common;

/// <summary>
/// Provides safe accessors for common JSON element shapes.
/// </summary>
public static class PluginJsonElement
{
    /// <summary>
    /// Gets a child object property from a JSON element.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="name">The property name to read.</param>
    /// <returns>The object property when present and of the correct kind; otherwise, <see langword="null"/>.</returns>
    public static JsonElement? GetObject(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Gets a child array property from a JSON element.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="name">The property name to read.</param>
    /// <returns>The array property when present and of the correct kind; otherwise, <see langword="null"/>.</returns>
    public static JsonElement? GetArray(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Gets a string property from a JSON element.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="name">The property name to read.</param>
    /// <returns>The string value when present and valid; otherwise, <see langword="null"/>.</returns>
    public static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Gets an integer property from a JSON element, accepting both numeric and string representations.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="name">The property name to read.</param>
    /// <returns>The parsed integer when present and valid; otherwise, <see langword="null"/>.</returns>
    public static int? GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Picks the first non-empty localized string from a language map, preferring English when available.
    /// </summary>
    /// <param name="map">The JSON object that contains language-to-string entries.</param>
    /// <returns>The selected string value, or <see langword="null"/> when no usable value exists.</returns>
    public static string? PickMapString(JsonElement? map)
    {
        if (map is null || map.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (map.Value.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            var value = en.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var property in map.Value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}