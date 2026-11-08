using EMMA.Domain;

namespace EMMA.WasmRuntime;

/// <summary>What one call to a plugin produced, besides its result.</summary>
/// <typeparam name="T">The result type.</typeparam>
/// <param name="Outcome">The result, or the failure.</param>
/// <param name="Trapped">Whether the plugin crashed or was stopped (CPU, memory or time). The host counts these.</param>
/// <param name="Logs">What the plugin logged.</param>
public sealed record PluginCall<T>(Outcome<T> Outcome, bool Trapped, IReadOnlyList<PluginLogEntry> Logs);
