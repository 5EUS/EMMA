using System.Text.Json.Serialization;

namespace EMMA.Runtime;

/// <summary>Source-generated JSON for the configuration document. A field the document does not define is an error.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RuntimeConfigDocument))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext
{
}
