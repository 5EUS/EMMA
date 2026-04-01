namespace EMMA.Plugin.Common;

public static class PluginWasmCliOperationDispatcher
{
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
