using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EMMA.Plugin.Common;

/// <summary>
/// Static utility class for provider-specific payload mappers.
/// Provides reusable patterns for parsing JSON payloads into domain models.
/// </summary>
public static class PluginPayloadMapperBase
{
    /// <summary>
    /// Safely extracts a property from a JsonElement, handling null/empty cases.
    /// </summary>
    public static string? GetString(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetString(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an object property from a JsonElement.
    /// </summary>
    public static JsonElement? GetObject(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetObject(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an array property from a JsonElement.
    /// </summary>
    public static JsonElement? GetArray(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetArray(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an int32 property from a JsonElement.
    /// </summary>
    public static int? GetInt32(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetInt32(element, propertyName);
    }

    /// <summary>
    /// Parses a chapter number from text, handling both integer and decimal formats.
    /// Falls back to fallbackNumber if parsing fails.
    /// </summary>
    public static int ParseChapterNumber(string? chapterText, int fallbackNumber)
    {
        if (string.IsNullOrWhiteSpace(chapterText))
        {
            return fallbackNumber;
        }

        if (int.TryParse(chapterText, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(chapterText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDecimal))
        {
            return (int)Math.Truncate(parsedDecimal);
        }

        return fallbackNumber;
    }

    /// <summary>
    /// Formats chapter title by combining chapter number and optional title text.
    /// If title contains "chapter" prefix, returns as-is; otherwise prepends "Chapter {number}".
    /// </summary>
    public static string FormatChapterTitle(
        string? chapterText,
        string? titleText,
        int number,
        Regex? chapterPrefixPattern = null)
    {
        var t = (titleText ?? "").Trim();
        var c = (chapterText ?? "").Trim();

        if (string.IsNullOrWhiteSpace(t))
        {
            return c.Length > 0 ? $"Chapter {c}" : $"Chapter {number}";
        }

        if (c.Length > 0)
        {
            // Check if title already contains "Chapter {n}" prefix
            if (chapterPrefixPattern?.IsMatch(t) ?? false)
            {
                return t;
            }

            return $"Chapter {c} · {t}";
        }

        return t;
    }

    /// <summary>
    /// Normalizes payload JSON by delegating to PluginJsonPayload.
    /// </summary>
    public static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    /// <summary>
    /// Creates a regex for detecting "Chapter {number}" prefixes in text.
    /// Used by FormatChapterTitle to avoid duplicating the prefix.
    /// </summary>
    public static Regex CreateChapterPrefixPattern()
    {
        return new Regex(
            @"(^|\b)chapter\s+\d+(?:\.\d+)?(\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// Extracts all string values from a JsonElement array.
    /// </summary>
    public static IReadOnlyList<string> ExtractStringArray(JsonElement? arrayElement)
    {
        if (arrayElement is null)
        {
            return [];
        }

        var results = new List<string>();
        foreach (var item in arrayElement.Value.EnumerateArray())
        {
            var str = item.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                results.Add(str);
            }
        }

        return results;
    }

    /// <summary>
    /// Builds a lookup dictionary from an array of objects with id/name properties.
    /// Useful for name lookups (e.g., scanlation groups, authors).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildNameLookup(
        JsonElement? arrayElement,
        string idPropertyName = "id",
        string namePropertyName = "name")
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (arrayElement is null)
        {
            return lookup;
        }

        foreach (var item in arrayElement.Value.EnumerateArray())
        {
            var id = GetString(item, idPropertyName);
            var name = GetString(item, namePropertyName);

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                lookup[id] = name;
            }
        }

        return lookup;
    }
}
