using System.Text.Json;

using EMMA.Domain;

namespace EMMA.Cli;

/// <summary>
/// Reads the values for a plugin's settings from a JSON file, for <c>emma plugin run --settings</c>: an object whose
/// members are setting keys and whose values are text, a number or boolean (kept as written), or a list of text for a
/// multi-choice setting.
/// </summary>
internal static class SettingsFile
{
    /// <summary>Reads a settings file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The changes it describes, or why it cannot be used.</returns>
    public static async Task<Outcome<IReadOnlyList<SettingChange>>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Fail($"{path} does not exist.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail($"{path} must hold a JSON object of setting keys and values.");
            }

            List<SettingChange> changes = [];
            foreach (JsonProperty member in Members(document.RootElement))
            {
                IReadOnlyList<string>? values = Values(member.Value);
                if (values is null)
                {
                    return Fail($"{path}: '{member.Name}' must be text, a number, a boolean or a list of text.");
                }

                changes.Add(new SettingChange(member.Name, values));
            }

            return Outcome.Success<IReadOnlyList<SettingChange>>(changes);
        }
        catch (JsonException ex)
        {
            return Fail($"{path} is not valid JSON: {ex.Message}");
        }
    }

    // The enumerators are disposable structs; materializing them keeps the loops plain.
    private static JsonProperty[] Members(JsonElement element) => [.. element.EnumerateObject()];

    private static JsonElement[] Items(JsonElement element) => [.. element.EnumerateArray()];

    private static List<string>? Values(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return [element.GetString()!];
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return [element.GetRawText()];
            case JsonValueKind.Array:
                List<string> items = [];
                foreach (JsonElement item in Items(element))
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    items.Add(item.GetString()!);
                }

                return items;
            default:
                return null;
        }
    }

    private static Outcome<IReadOnlyList<SettingChange>> Fail(string message) =>
        Outcome.Failure<IReadOnlyList<SettingChange>>(ErrorCode.InvalidArgument, message);
}
