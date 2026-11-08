using EMMA.Domain;

namespace EMMA.WasmRuntime.Wire;

/// <summary>A plugin failure.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">The message.</param>
/// <param name="RetryAfterMs">When to retry, in milliseconds.</param>
internal sealed record WireError(ErrorCode Code, string Message, uint? RetryAfterMs);
