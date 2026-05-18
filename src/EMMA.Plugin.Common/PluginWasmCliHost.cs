namespace EMMA.Plugin.Common;

/// <summary>
/// Hosts a single-operation WASM CLI execution loop.
/// </summary>
public static class PluginWasmCliHost
{
    /// <summary>
    /// Runs the WASM CLI host loop for a single operation invocation.
    /// </summary>
    /// <param name="args">The raw CLI arguments.</param>
    /// <param name="knownOperations">The set of supported operation names.</param>
    /// <param name="executeOperation">The callback that executes the normalized operation.</param>
    /// <param name="readPayload">An optional callback that reads input payload text.</param>
    /// <param name="emitPayloadDiagnostics">An optional callback that emits payload diagnostics.</param>
    /// <param name="output">An optional writer for normal output.</param>
    /// <param name="error">An optional writer for error output.</param>
    /// <returns><c>0</c> on success, <c>1</c> on host failure, or <c>2</c> when the operation is unsupported or invalid.</returns>
    public static int Run(
        string[] args,
        IReadOnlySet<string> knownOperations,
        Func<string, string[], string, string> executeOperation,
        Func<string>? readPayload = null,
        Action<string, string>? emitPayloadDiagnostics = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        try
        {
            var (operation, operationArgs) = PluginCliOperations.NormalizeOperationArgs(args, knownOperations);
            var inputPayload = (readPayload ?? ReadInputPayloadNonBlocking)();
            (emitPayloadDiagnostics ?? PluginPayload.EmitPayloadDiagnostics)(operation, inputPayload);

            var json = executeOperation(operation, operationArgs, inputPayload);
            if (string.IsNullOrWhiteSpace(json))
            {
                error.WriteLine("Unsupported or invalid operation.");
                return 2;
            }

            output.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"WASM CLI host failed: {ex}");
            return 1;
        }
    }

    private static string ReadInputPayloadNonBlocking()
    {
        try
        {
            return Console.IsInputRedirected
                ? PluginPayload.ReadInputPayload()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}