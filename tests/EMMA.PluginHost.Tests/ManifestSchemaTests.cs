using System.Text.Json;

using EMMA.TestSupport;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>
/// Keeps <c>plugin-manifest.schema.json</c> honest. The schema is documentation (the parser is the authority), so
/// nothing else would notice if the two drifted apart.
/// </summary>
public sealed class ManifestSchemaTests
{
    /// <summary>Everything the manifest writer emits is a property the schema declares, at every level.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EveryWrittenFieldIsDeclaredBySchema()
    {
        using JsonDocument schema = await LoadSchema().ConfigureAwait(true);
        using JsonDocument written = JsonDocument.Parse(TestManifests.Json(TestManifests.Valid()));

        List<string> problems = [];
        Compare(schema.RootElement, written.RootElement, "$", problems);

        Assert.Empty(problems);
    }

    /// <summary>The schema is a closed document at every level, so an undeclared field is never silently valid.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SchemaRefusesUndeclaredFields()
    {
        using JsonDocument schema = await LoadSchema().ConfigureAwait(true);

        List<string> open = [];
        FindOpenObjects(schema.RootElement, "$", open);

        Assert.Empty(open);
    }

    private static async Task<JsonDocument> LoadSchema()
    {
        string text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plugin-manifest.schema.json")).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    // JsonElement's enumerators are disposable structs; materializing them keeps the loops below plain.
    private static JsonProperty[] Members(JsonElement element) => [.. element.EnumerateObject()];

    private static JsonElement[] Items(JsonElement element) => [.. element.EnumerateArray()];

    private static void Compare(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        JsonElement properties = schema.GetProperty("properties");
        foreach (JsonProperty field in Members(value))
        {
            if (!properties.TryGetProperty(field.Name, out JsonElement declared))
            {
                problems.Add($"{path}.{field.Name} is written but the schema does not declare it");
            }
            else if (field.Value.ValueKind == JsonValueKind.Object)
            {
                Compare(declared, field.Value, $"{path}.{field.Name}", problems);
            }
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in Items(required))
            {
                if (!value.TryGetProperty(name.GetString()!, out _))
                {
                    problems.Add($"{path}.{name.GetString()} is required by the schema but not written");
                }
            }
        }
    }

    private static void FindOpenObjects(JsonElement schema, string path, List<string> open)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties))
        {
            return;
        }

        if (!schema.TryGetProperty("additionalProperties", out JsonElement additional) || additional.ValueKind != JsonValueKind.False)
        {
            open.Add(path);
        }

        foreach (JsonProperty property in Members(properties))
        {
            FindOpenObjects(property.Value, $"{path}.{property.Name}", open);
        }
    }
}
