using System.Text.Json;

using EMMA.Domain;

namespace EMMA.Storage;

/// <summary>Converts props to and from the JSON text stored with a node.</summary>
internal static class PropCodec
{
    /// <summary>Encodes props as JSON text.</summary>
    /// <param name="props">The props, in order.</param>
    /// <returns>The JSON array.</returns>
    public static string Encode(IReadOnlyList<Prop> props)
    {
        StoredProp[] stored = [.. props.Select(ToStored)];
        return JsonSerializer.Serialize(stored, StorageJsonContext.Default.StoredPropArray);
    }

    /// <summary>Decodes props from stored JSON text.</summary>
    /// <param name="json">The JSON array written by <see cref="Encode"/>.</param>
    /// <returns>The props, in the stored order.</returns>
    /// <exception cref="InvalidDataException">The stored text is not a valid prop list.</exception>
    public static IReadOnlyList<Prop> Decode(string json)
    {
        StoredProp[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize(json, StorageJsonContext.Default.StoredPropArray);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A stored prop list is corrupt.", ex);
        }

        return stored is null ? throw new InvalidDataException("A stored prop list is null.") : [.. stored.Select(FromStored)];
    }

    private static StoredProp ToStored(Prop prop)
    {
        PropValue value = prop.Value;
        return value.Kind switch
        {
            PropValueKind.Text or PropValueKind.Url => new StoredProp(prop.Key, value.Kind, value.Text, null, null, null),
            PropValueKind.WholeNumber or PropValueKind.Timestamp or PropValueKind.Flag =>
                new StoredProp(prop.Key, value.Kind, null, value.WholeNumber, null, null),
            PropValueKind.Number => new StoredProp(prop.Key, value.Kind, null, null, value.Number, null),
            PropValueKind.TextList => new StoredProp(prop.Key, value.Kind, null, null, null, [.. value.TextList]),
            _ => throw new InvalidDataException($"Unknown prop kind {value.Kind}."),
        };
    }

    private static Prop FromStored(StoredProp stored)
    {
        PropValue value = stored.Kind switch
        {
            PropValueKind.Text => PropValue.FromText(stored.Text ?? string.Empty),
            PropValueKind.Url => PropValue.FromUrl(new Uri(stored.Text ?? string.Empty, UriKind.Absolute)),
            PropValueKind.WholeNumber => PropValue.FromWholeNumber(stored.Whole ?? 0),
            PropValueKind.Timestamp => PropValue.FromTimestamp(stored.Whole ?? 0),
            PropValueKind.Flag => PropValue.FromFlag((stored.Whole ?? 0) != 0),
            PropValueKind.Number => PropValue.FromNumber(stored.Number ?? 0),
            PropValueKind.TextList => PropValue.FromTextList(stored.List ?? []),
            _ => throw new InvalidDataException($"Unknown prop kind {stored.Kind}."),
        };
        return new Prop(stored.Key, value);
    }
}
