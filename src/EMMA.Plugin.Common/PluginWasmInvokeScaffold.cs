using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Serializes invoke-style WASM operations for CLI and host execution.
/// </summary>
public static class PluginWasmInvokeScaffold
{
    /// <summary>
    /// Serializes an invoke operation result for CLI execution.
    /// </summary>
    /// <param name="args">The CLI arguments that describe the invoke request.</param>
    /// <param name="stdinPayload">The payload text read from standard input.</param>
    /// <param name="invoke">The invoke handler to execute.</param>
    /// <param name="operationResultTypeInfo">The JSON type metadata for the operation result.</param>
    /// <returns>The serialized operation result JSON.</returns>
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

    /// <summary>
    /// Builds a successful JSON operation result for an already serialized payload.
    /// </summary>
    /// <param name="payloadJson">The JSON payload to wrap.</param>
    /// <returns>An operation result with <c>application/json</c> content.</returns>
    public static OperationResult BuildJsonResult(string payloadJson)
    {
        return new OperationResult(false, null, "application/json", payloadJson);
    }

    /// <summary>
    /// Serializes a typed payload into a successful JSON operation result.
    /// </summary>
    /// <param name="payload">The typed payload to serialize.</param>
    /// <param name="typeInfo">The JSON type metadata used to serialize the payload.</param>
    /// <returns>An operation result with <c>application/json</c> content.</returns>
    public static OperationResult BuildJsonResult<T>(T payload, JsonTypeInfo<T> typeInfo)
    {
        return BuildJsonResult(JsonSerializer.Serialize(payload, typeInfo));
    }

    /// <summary>
    /// Serializes an optional typed payload into a successful JSON operation result.
    /// </summary>
    /// <param name="payload">The optional payload to serialize.</param>
    /// <param name="typeInfo">The JSON type metadata used to serialize the payload.</param>
    /// <returns>A JSON null result when <paramref name="payload"/> is null; otherwise a serialized JSON operation result.</returns>
    public static OperationResult BuildNullableJsonResult<T>(T? payload, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        return payload is null
            ? BuildJsonResult("null")
            : BuildJsonResult(payload, typeInfo);
    }
}