namespace EMMA.WasmRuntime.Wire;

/// <summary>One line the plugin logged.</summary>
/// <param name="Level">The level name.</param>
/// <param name="Message">The message.</param>
internal sealed record WireLog(string Level, string Message);
