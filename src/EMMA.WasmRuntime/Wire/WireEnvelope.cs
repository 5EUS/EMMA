namespace EMMA.WasmRuntime.Wire;

/// <summary>What the host returns for every call.</summary>
/// <typeparam name="T">The shape of a successful result.</typeparam>
/// <param name="Ok">The result, when the call succeeded.</param>
/// <param name="Error">The failure, when it did not.</param>
/// <param name="Trapped">Whether the plugin trapped.</param>
/// <param name="Logs">What the plugin logged.</param>
internal sealed record WireEnvelope<T>(T? Ok, WireError? Error, bool Trapped, IReadOnlyList<WireLog> Logs);
