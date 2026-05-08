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
    /// Parses an array property into a list of mapped struct values.
    /// </summary>
    /// <param name="root">The root JSON object that contains the array property.</param>
    /// <param name="propertyName">The name of the array property to parse.</param>
    /// <param name="selector">The mapper that converts each element into a nullable struct value.</param>
    /// <returns>The mapped struct values extracted from the array.</returns>
    public static IReadOnlyList<TResult> ParseStructArray<TResult>(
        JsonElement root,
        string propertyName,
        Func<JsonElement, TResult?> selector)
        where TResult : struct
    {
        return ParseStructArray(GetArray(root, propertyName), selector);
    }

    /// <summary>
    /// Parses an optional JSON array into a list of mapped struct values.
    /// </summary>
    /// <param name="arrayElement">The array element to parse.</param>
    /// <param name="selector">The mapper that converts each element into a nullable struct value.</param>
    /// <returns>The mapped struct values extracted from the array.</returns>
    public static IReadOnlyList<TResult> ParseStructArray<TResult>(
        JsonElement? arrayElement,
        Func<JsonElement, TResult?> selector)
        where TResult : struct
    {
        if (arrayElement is null || arrayElement.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<TResult>();
        foreach (var item in arrayElement.Value.EnumerateArray())
        {
            var mapped = selector(item);
            if (mapped.HasValue)
            {
                results.Add(mapped.Value);
            }
        }

        return results;
    }

    /// <summary>
    /// Parses an object property whose child properties each map to a metadata list.
    /// </summary>
    /// <param name="root">The root JSON object that contains the metadata object.</param>
    /// <param name="propertyName">The name of the object property to parse.</param>
    /// <param name="selector">The mapper that converts each object property into a metadata list.</param>
    /// <returns>A dictionary keyed by property name for entries that produced metadata.</returns>
    public static IReadOnlyDictionary<string, List<TMetadata>> ParseObjectMetadataByKey<TMetadata>(
        JsonElement root,
        string propertyName,
        Func<JsonProperty, List<TMetadata>> selector)
    {
        var metadataById = new Dictionary<string, List<TMetadata>>(StringComparer.OrdinalIgnoreCase);
        var obj = GetObject(root, propertyName);
        if (obj is null)
        {
            return metadataById;
        }

        foreach (var property in obj.Value.EnumerateObject())
        {
            var metadata = selector(property);
            if (metadata.Count > 0)
            {
                metadataById[property.Name] = metadata;
            }
        }

        return metadataById;
    }

    /// <summary>
    /// Safely extracts a property from a JsonElement, handling null/empty cases.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The string property value when present and valid; otherwise, <see langword="null"/>.</returns>
    public static string? GetString(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetString(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an object property from a JsonElement.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The object property when present and valid; otherwise, <see langword="null"/>.</returns>
    public static JsonElement? GetObject(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetObject(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an array property from a JsonElement.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The array property when present and valid; otherwise, <see langword="null"/>.</returns>
    public static JsonElement? GetArray(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetArray(element, propertyName);
    }

    /// <summary>
    /// Safely extracts an int32 property from a JsonElement.
    /// </summary>
    /// <param name="element">The JSON element that owns the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The parsed integer when present and valid; otherwise, <see langword="null"/>.</returns>
    public static int? GetInt32(JsonElement element, string propertyName)
    {
        return PluginJsonElement.GetInt32(element, propertyName);
    }

    /// <summary>
    /// Parses a chapter number from text, handling both integer and decimal formats.
    /// Falls back to fallbackNumber if parsing fails.
    /// </summary>
    /// <param name="chapterText">The chapter number text to parse.</param>
    /// <param name="fallbackNumber">The fallback chapter number to use when parsing fails.</param>
    /// <returns>The parsed chapter number, or <paramref name="fallbackNumber"/> when parsing fails.</returns>
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
    /// <param name="chapterText">The source chapter number text.</param>
    /// <param name="titleText">The optional chapter title text.</param>
    /// <param name="number">The fallback numeric chapter number.</param>
    /// <param name="chapterPrefixPattern">An optional regex used to detect an existing chapter prefix in the title.</param>
    /// <returns>The formatted chapter title.</returns>
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
    /// <param name="payload">The payload text to normalize.</param>
    /// <returns>The normalized payload content.</returns>
    public static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    /// <summary>
    /// Creates a regex for detecting "Chapter {number}" prefixes in text.
    /// Used by FormatChapterTitle to avoid duplicating the prefix.
    /// </summary>
    /// <returns>A compiled regex that matches chapter-prefix text.</returns>
    public static Regex CreateChapterPrefixPattern()
    {
        return new Regex(
            @"(^|\b)chapter\s+\d+(?:\.\d+)?(\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// Extracts all string values from a JsonElement array.
    /// </summary>
    /// <param name="arrayElement">The array element to inspect.</param>
    /// <returns>The non-empty string values found in the array.</returns>
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
    /// <param name="arrayElement">The array of objects to inspect.</param>
    /// <param name="idPropertyName">The property name that contains the identifier.</param>
    /// <param name="namePropertyName">The property name that contains the display name.</param>
    /// <returns>A case-insensitive lookup of identifiers to names.</returns>
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
