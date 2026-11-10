using System.Text.Json.Serialization;

namespace EMMA.WasmRuntime.Wire;

/// <summary>What <c>emma_wasm_plugin_open</c> reports.</summary>
/// <param name="SearchAssist">Whether the plugin exports the optional search-assist interface.</param>
internal sealed record WireOpenInfo([property: JsonPropertyName("searchAssist")] bool SearchAssist);
