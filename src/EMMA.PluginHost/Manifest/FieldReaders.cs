using System.Globalization;

namespace EMMA.PluginHost.Manifest;

/// <summary>Field readers shared by the manifest and delegation-file parsers.</summary>
internal static class FieldReaders
{
    /// <summary>Reads a required repository or key identifier.</summary>
    /// <param name="fields">The object being read.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The identifier, or whatever was read after recording a problem.</returns>
    public static string Identifier(JsonFields fields, string name)
    {
        string value = fields.RequireString(name, 128);
        if (!ManifestRules.IsIdentifier(value))
        {
            fields.Reject(name, "must be 1 to 128 letters, digits, '.', '_' or '-', starting with a letter or digit");
        }

        return value;
    }

    /// <summary>Reads a required ISO 8601 timestamp and normalizes it to UTC.</summary>
    /// <param name="fields">The object being read.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The timestamp, or the default value after recording a problem.</returns>
    public static DateTimeOffset Timestamp(JsonFields fields, string name)
    {
        string text = fields.RequireString(name, 40);
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            || DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return parsed.ToUniversalTime();
        }

        fields.Reject(name, "must be an ISO 8601 date-time with an offset, such as 2026-01-01T00:00:00Z");
        return default;
    }

    /// <summary>Checks that text is non-empty, valid base64.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it decodes.</returns>
    public static bool IsBase64(string value)
    {
        Span<byte> scratch = new byte[(value.Length * 3 / 4) + 3];
        return value.Length > 0 && Convert.TryFromBase64String(value, scratch, out _);
    }
}
