using System.Text.Json.Serialization;

namespace EMMA.Storage;

/// <summary>Source-generated JSON for the few structures storage serializes: no reflection, safe for NativeAOT.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoredProp[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
