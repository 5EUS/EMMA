using System.Text.Json.Serialization;

namespace EMMA.WasmRuntime.Wire;

/// <summary>A condition under which a setting is shown.</summary>
/// <param name="Key">The other setting's key.</param>
/// <param name="EqualsValue">The value it must have.</param>
internal sealed record WireCondition(string Key, [property: JsonPropertyName("equals")] string EqualsValue);
