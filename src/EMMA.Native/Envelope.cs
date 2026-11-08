using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Native;

/// <summary>Builds the JSON every call answers with: <c>{"ok": result}</c> or <c>{"error": {"code", "message", "retry_after_ms"}}</c>.</summary>
internal static class Envelope
{
    private static readonly byte[] OkPrefix = "{\"ok\":"u8.ToArray();

    /// <summary>Gets the response for an operation that succeeds with nothing to return.</summary>
    public static NativeResponse Done { get; } = new("{\"ok\":{}}"u8.ToArray());

    /// <summary>Wraps a result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="value">The result.</param>
    /// <param name="typeInfo">How to write the result.</param>
    /// <param name="blob">Binary content that goes beside the JSON.</param>
    /// <returns>The response.</returns>
    public static NativeResponse Ok<T>(T value, JsonTypeInfo<T> typeInfo, byte[]? blob = null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        byte[] json = new byte[OkPrefix.Length + body.Length + 1];
        OkPrefix.CopyTo(json, 0);
        body.CopyTo(json, OkPrefix.Length);
        json[^1] = (byte)'}';
        return new NativeResponse(json, blob);
    }

    /// <summary>Describes a pipeline or plugin failure.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>The response.</returns>
    public static NativeResponse Failure(PluginError error) => Fault(ApiErrors.ToResponse(error));

    /// <summary>Describes a failure the library itself found.</summary>
    /// <param name="code">The failure class.</param>
    /// <param name="message">A message that is safe to show.</param>
    /// <returns>The response.</returns>
    public static NativeResponse Failure(ErrorCode code, string message) => Failure(new PluginError(code, message));

    /// <summary>Describes a failure with a code of the library's own, such as <c>internal_error</c>.</summary>
    /// <param name="code">The stable machine-readable code.</param>
    /// <param name="message">A message that is safe to show.</param>
    /// <returns>The response.</returns>
    public static NativeResponse Fault(string code, string message) => Fault(ApiErrors.ToResponse(code, message));

    private static NativeResponse Fault(ErrorResponse body) =>
        new(JsonSerializer.SerializeToUtf8Bytes(body, ApiJsonContext.Default.ErrorResponse));
}
