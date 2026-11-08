using System.Text.Json.Serialization;

namespace EMMA.WasmRuntime.Wire;

/// <summary>Source-generated serialization for the host's camelCase configuration JSON.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(RuntimeOptionsDocument))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
