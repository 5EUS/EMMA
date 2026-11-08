using System.Text.Json;

namespace EMMA.PluginHost.Manifest;

/// <summary>
/// A strict reader for one JSON object. Every property must be read or the object is
/// rejected (<see cref="Finish"/>), so nothing unsigned can ride along in a signed document.
/// Problems are recorded on the shared <see cref="ParseErrors"/> instead of thrown, and the
/// accessors return a harmless default so parsing can continue to the first real report.
/// </summary>
internal sealed class JsonFields
{
    private readonly JsonElement element;
    private readonly string path;
    private readonly ParseErrors errors;
    private readonly HashSet<string> read = new(StringComparer.Ordinal);

    private JsonFields(JsonElement element, string path, ParseErrors errors)
    {
        this.element = element;
        this.path = path;
        this.errors = errors;
    }

    /// <summary>Starts reading the root object.</summary>
    /// <param name="root">The root element.</param>
    /// <param name="errors">Where problems go.</param>
    /// <returns>The reader, or <see langword="null"/> when the root is not an object.</returns>
    public static JsonFields? Root(JsonElement root, ParseErrors errors) => From(root, "$", errors);

    /// <summary>Reads a required string.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="maxLength">The longest accepted value.</param>
    /// <returns>The value, or an empty string after recording a problem.</returns>
    public string RequireString(string name, int maxLength) =>
        this.TryGet(name, out JsonElement value)
            ? ReadString(value, this.Path(name), maxLength, this.errors) ?? string.Empty
            : this.Missing(name, string.Empty);

    /// <summary>Reads an optional string.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="maxLength">The longest accepted value.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public string? OptionalString(string name, int maxLength) =>
        this.TryGet(name, out JsonElement value) ? ReadString(value, this.Path(name), maxLength, this.errors) : null;

    /// <summary>Reads a required whole number within a range.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="min">The smallest accepted value.</param>
    /// <param name="max">The largest accepted value.</param>
    /// <returns>The value, or zero after recording a problem.</returns>
    public long RequireInteger(string name, long min, long max) =>
        this.TryGet(name, out JsonElement value) ? this.ReadInteger(value, name, min, max) : this.Missing(name, 0L);

    /// <summary>Reads an optional whole number within a range.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="min">The smallest accepted value.</param>
    /// <param name="max">The largest accepted value.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public long? OptionalInteger(string name, long min, long max) =>
        this.TryGet(name, out JsonElement value) ? this.ReadInteger(value, name, min, max) : null;

    /// <summary>Reads an optional finite number above zero.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public double? OptionalPositiveNumber(string name)
    {
        if (!this.TryGet(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number > 0)
        {
            return number;
        }

        this.errors.Add(this.Path(name), "must be a number above zero");
        return null;
    }

    /// <summary>Reads a required boolean.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="false"/> after recording a problem.</returns>
    public bool RequireBoolean(string name)
    {
        if (!this.TryGet(name, out JsonElement value))
        {
            return this.Missing(name, false);
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        this.errors.Add(this.Path(name), "must be true or false");
        return false;
    }

    /// <summary>Reads a required array of strings.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="maxItems">The most items accepted.</param>
    /// <param name="itemMaxLength">The longest accepted item.</param>
    /// <returns>The items, or an empty list after recording a problem.</returns>
    public IReadOnlyList<string> RequireStringArray(string name, int maxItems, int itemMaxLength) =>
        this.TryGet(name, out JsonElement value) ? this.ReadStrings(value, name, maxItems, itemMaxLength) : this.Missing<IReadOnlyList<string>>(name, []);

    /// <summary>Reads an optional array of strings.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="maxItems">The most items accepted.</param>
    /// <param name="itemMaxLength">The longest accepted item.</param>
    /// <returns>The items, or an empty list when absent.</returns>
    public IReadOnlyList<string> OptionalStringArray(string name, int maxItems, int itemMaxLength) =>
        this.TryGet(name, out JsonElement value) ? this.ReadStrings(value, name, maxItems, itemMaxLength) : [];

    /// <summary>Reads a required nested object.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The nested reader, or <see langword="null"/> after recording a problem.</returns>
    public JsonFields? RequireObject(string name) =>
        this.TryGet(name, out JsonElement value) ? From(value, this.Path(name), this.errors) : this.Missing<JsonFields?>(name, null);

    /// <summary>Reads an optional nested object.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The nested reader, or <see langword="null"/> when absent.</returns>
    public JsonFields? OptionalObject(string name) =>
        this.TryGet(name, out JsonElement value) ? From(value, this.Path(name), this.errors) : null;

    /// <summary>Reads a required array of objects, calling <paramref name="map"/> for each.</summary>
    /// <typeparam name="T">The mapped type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <param name="maxItems">The most items accepted.</param>
    /// <param name="map">Maps one item's reader to a value.</param>
    /// <returns>The mapped items, or an empty list after recording a problem.</returns>
    public IReadOnlyList<T> RequireObjectArray<T>(string name, int maxItems, Func<JsonFields, T> map)
    {
        if (!this.TryGet(name, out JsonElement value))
        {
            return this.Missing<IReadOnlyList<T>>(name, []);
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maxItems)
        {
            this.errors.Add(this.Path(name), $"must be an array of at most {maxItems} objects");
            return [];
        }

        List<T> items = [];
        for (int index = 0; index < value.GetArrayLength(); index++)
        {
            JsonFields? fields = From(value[index], $"{this.Path(name)}[{index}]", this.errors);
            if (fields is not null)
            {
                items.Add(map(fields));
            }
        }

        return items;
    }

    /// <summary>Records a semantic problem with a property that was read successfully.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="message">What is wrong.</param>
    public void Reject(string name, string message) => this.errors.Add(this.Path(name), message);

    /// <summary>Rejects the object if it has any property that was not read.</summary>
    public void Finish()
    {
        using JsonElement.ObjectEnumerator properties = this.element.EnumerateObject();
        foreach (string name in properties.Select(property => property.Name).Where(name => !this.read.Contains(name)))
        {
            this.errors.Add(this.Path(name), "is not allowed");
        }
    }

    private static JsonFields? From(JsonElement element, string path, ParseErrors errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return new JsonFields(element, path, errors);
        }

        errors.Add(path, "must be an object");
        return null;
    }

    private static string? ReadString(JsonElement value, string path, int maxLength, ParseErrors errors)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maxLength)
        {
            return text;
        }

        errors.Add(path, $"must be a string of at most {maxLength} characters");
        return null;
    }

    private bool TryGet(string name, out JsonElement value)
    {
        this.read.Add(name);
        return this.element.TryGetProperty(name, out value);
    }

    private string Path(string name) => $"{this.path}.{name}";

    private T Missing<T>(string name, T fallback)
    {
        this.errors.Add(this.Path(name), "is required");
        return fallback;
    }

    private long ReadInteger(JsonElement value, string name, long min, long max)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= min && number <= max)
        {
            return number;
        }

        this.errors.Add(this.Path(name), $"must be a whole number from {min} to {max}");
        return 0;
    }

    private List<string> ReadStrings(JsonElement value, string name, int maxItems, int itemMaxLength)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maxItems)
        {
            this.errors.Add(this.Path(name), $"must be an array of at most {maxItems} strings");
            return [];
        }

        List<string> items = [];
        for (int index = 0; index < value.GetArrayLength(); index++)
        {
            string? text = ReadString(value[index], $"{this.Path(name)}[{index}]", itemMaxLength, this.errors);
            if (text is not null)
            {
                items.Add(text);
            }
        }

        return items;
    }
}
