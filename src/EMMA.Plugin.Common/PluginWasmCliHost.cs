namespace EMMA.Plugin.Common;

public static class PluginWasmCliHost
{
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