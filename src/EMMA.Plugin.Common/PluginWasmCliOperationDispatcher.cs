namespace EMMA.Plugin.Common;

/// <summary>
/// Dispatches normalized CLI operations to WASM handlers.
/// </summary>
public static class PluginWasmCliOperationDispatcher
{
    /// <summary>
    /// Executes a CLI operation handler and captures failures as empty results.
    /// </summary>
    /// <param name="operation">The operation name to execute.</param>
    /// <param name="args">The operation-specific CLI arguments.</param>
    /// <param name="inputPayload">The payload text read from standard input.</param>
    /// <param name="handlers">The available CLI operation handlers keyed by operation name.</param>
    /// <param name="error">An optional writer for execution errors.</param>
    /// <returns>The handler output, or an empty string when the operation is missing or execution fails.</returns>
    public static string Execute(
        string operation,
        string[] args,
        string inputPayload,
        IReadOnlyDictionary<string, Func<string[], string, string>> handlers,
        TextWriter? error = null)
    {
        error ??= Console.Error;

        try
        {
            return handlers.TryGetValue(operation, out var handler)
                ? handler(args, inputPayload)
                : string.Empty;
        }
        catch (Exception ex)
        {
            error.WriteLine($"WASM operation '{operation}' failed: {ex}");
            return string.Empty;
        }
    }
}
