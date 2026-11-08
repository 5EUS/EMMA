using System.Text.Json.Serialization;

namespace EMMA.WasmRuntime.Wire;

/// <summary>A typed property value, externally tagged: exactly one member is set.</summary>
/// <param name="Text">A text value.</param>
/// <param name="Integer">A whole number.</param>
/// <param name="Number">A double.</param>
/// <param name="Flag">A boolean.</param>
/// <param name="TimestampMs">Milliseconds since the Unix epoch.</param>
/// <param name="Url">An absolute URL.</param>
/// <param name="TextList">A list of text values.</param>
internal sealed record WirePropValue(
    [property: JsonPropertyName("Text")] string? Text,
    [property: JsonPropertyName("Integer")] long? Integer,
    [property: JsonPropertyName("Number")] double? Number,
    [property: JsonPropertyName("Flag")] bool? Flag,
    [property: JsonPropertyName("TimestampMs")] long? TimestampMs,
    [property: JsonPropertyName("Url")] string? Url,
    [property: JsonPropertyName("TextList")] IReadOnlyList<string>? TextList);
