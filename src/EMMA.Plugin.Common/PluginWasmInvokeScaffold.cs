using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public static class PluginWasmInvokeScaffold
{
    public static string SerializeInvokeForCli(
        string[] args,
        string stdinPayload,
        Func<OperationRequest, OperationResult> invoke,
        JsonTypeInfo<OperationResult> operationResultTypeInfo)
    {
        if (args.Length == 0)
        {
            return JsonSerializer.Serialize(OperationResult.InvalidArguments("missing operation"), operationResultTypeInfo);
        }

        var request = new OperationRequest(
            args[0],
            args.Length > 1 ? args[1] : null,
            args.Length > 2 ? args[2] : null,
            args.Length > 3 ? args[3] : null,
            stdinPayload);

        return JsonSerializer.Serialize(invoke(request), operationResultTypeInfo);
    }

    public static OperationResult BuildJsonResult(string payloadJson)
    {
        return new OperationResult(false, null, "application/json", payloadJson);
    }
}