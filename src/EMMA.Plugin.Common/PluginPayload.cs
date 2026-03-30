namespace EMMA.Plugin.Common;

public static class PluginPayload
{
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

    public static void EmitPayloadDiagnostics(string operation, string payload)
    {
        if (!PluginEnvironment.ShouldLogPayloadDiagnostics())
        {
            return;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload ?? string.Empty);
        Console.Error.WriteLine($"[TEMP_TIMING_REMOVE] wasmPayload op={operation} source=stdin bytes={payloadBytes}");
    }

    public static string NormalizePayload(string? payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

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
