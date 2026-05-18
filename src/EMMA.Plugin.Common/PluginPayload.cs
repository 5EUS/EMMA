namespace EMMA.Plugin.Common;

/// <summary>
/// Reads, normalizes, and resolves plugin operation payloads.
/// </summary>
public static class PluginPayload
{
    /// <summary>
    /// Reads the full payload from standard input, returning an empty string when input cannot be read.
    /// </summary>
    /// <returns>The input payload text, or an empty string on failure.</returns>
    public static string ReadInputPayload()
    {
        try
        {
            return Console.In.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Emits payload size diagnostics for an operation when payload logging is enabled.
    /// </summary>
    /// <param name="operation">The operation associated with the payload.</param>
    /// <param name="payload">The payload content whose size should be logged.</param>
    public static void EmitPayloadDiagnostics(string operation, string payload)
    {
        if (!PluginEnvironment.ShouldLogPayloadDiagnostics())
        {
            return;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload ?? string.Empty);
        Console.Error.WriteLine($"[TEMP_TIMING_REMOVE] wasmPayload op={operation} source=stdin bytes={payloadBytes}");
    }

    /// <summary>
    /// Normalizes a payload string into clean JSON content.
    /// </summary>
    /// <param name="payload">The payload to normalize.</param>
    /// <returns>The normalized payload text.</returns>
    public static string NormalizePayload(string? payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    /// <summary>
    /// Resolves a payload from the provided value or, when missing, from a fallback provider.
    /// </summary>
    /// <param name="payload">The primary payload value.</param>
    /// <param name="fallbackProvider">The callback used to fetch a fallback payload when the primary value is empty.</param>
    /// <returns>The normalized payload text resolved from the provided value or fallback source.</returns>
    public static string ResolvePayload(string? payload, Func<string?> fallbackProvider)
    {
        var normalized = NormalizePayload(payload);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return NormalizePayload(fallbackProvider());
    }
}
