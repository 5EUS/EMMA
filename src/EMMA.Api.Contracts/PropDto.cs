using System.Text.Json.Nodes;

namespace EMMA.Api.Contracts;

/// <summary>A typed property.</summary>
/// <param name="Key">The key.</param>
/// <param name="Type"><c>text</c>, <c>whole_number</c>, <c>number</c>, <c>flag</c>, <c>timestamp</c>, <c>url</c> or <c>text_list</c>.</param>
/// <param name="Value">A string, number, boolean or array of strings, matching <c>type</c>; timestamps are Unix milliseconds.</param>
public sealed record PropDto(
    string Key,
    string Type,
    JsonNode? Value);
