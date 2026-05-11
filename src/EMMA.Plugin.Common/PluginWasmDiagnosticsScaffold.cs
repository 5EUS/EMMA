using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Provides reusable diagnostics and benchmark helpers for WASM plugins.
/// </summary>
public static class PluginWasmDiagnosticsScaffold
{
    /// <summary>
    /// Writes a development-only diagnostic line when plugin development mode is enabled.
    /// </summary>
    /// <param name="message">The diagnostic message to write.</param>
    public static void DevLog(string message)
    {
        if (PluginEnvironment.IsDevelopmentMode())
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Emits search timing diagnostics when plugin timing diagnostics are enabled.
    /// </summary>
    public static void EmitSearchSplitTiming(
        string query,
        string payload,
        long fetchMs,
        long parseMs,
        long mapMs,
        int resultCount,
        bool payloadWasFetched,
        long totalMs)
    {
        if (!ShouldLogPluginTimingDiagnostics())
        {
            return;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload ?? string.Empty);
        Console.Error.WriteLine(
            "[TEMP_TIMING_REMOVE] pluginSearch op=search queryLength={0} payloadSource={1} fetchMs={2} parseMs={3} mapMs={4} totalMs={5} payloadBytes={6} resultCount={7}",
            query?.Length ?? 0,
            payloadWasFetched ? "provider" : "provided",
            fetchMs,
            parseMs,
            mapMs,
            totalMs,
            payloadBytes,
            resultCount);
    }

    /// <summary>
    /// Executes the standard CPU benchmark used by WASM plugins and serializes the result.
    /// </summary>
    public static string RunCpuBenchmark(string[] args, JsonTypeInfo<BenchmarkResult> benchmarkTypeInfo)
    {
        var iterations = 5000;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0)
        {
            iterations = Math.Clamp(parsed, 1, 1_000_000);
        }

        var stopwatch = Stopwatch.StartNew();
        long checksum = 1469598103934665603;
        const ulong prime = 1099511628211;
        var generated = 0;

        for (var i = 0; i < iterations; i++)
        {
            var text = $"bench:{i}:{i * 31 % 97}";
            foreach (var rune in text.EnumerateRunes())
            {
                checksum ^= rune.Value;
                checksum = (long)((ulong)checksum * prime);
            }

            generated += text.Length;
        }

        stopwatch.Stop();

        return JsonSerializer.Serialize(
            new BenchmarkResult(iterations, checksum, generated, stopwatch.ElapsedMilliseconds),
            benchmarkTypeInfo);
    }

    /// <summary>
    /// Executes the standard network benchmark used by WASM plugins and serializes the result.
    /// </summary>
    public static string RunNetworkBenchmark(
        string[] args,
        string stdinPayload,
        Func<PluginSearchQuery, string> resolvePayload,
        JsonTypeInfo<NetworkBenchmarkResult> benchmarkTypeInfo)
    {
        var query = args.Length > 0 ? args[0] : "one piece";
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        var payloadJson = resolvePayload(parsedQuery);

        var stopwatch = Stopwatch.StartNew();
        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payloadJson ?? string.Empty);
        var itemCount = 0;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var data = PluginJsonElement.GetArray(doc.RootElement, "data");
            itemCount = data?.GetArrayLength() ?? 0;
        }

        stopwatch.Stop();

        return JsonSerializer.Serialize(
            new NetworkBenchmarkResult(parsedQuery.Query, payloadBytes, itemCount, stopwatch.ElapsedMilliseconds),
            benchmarkTypeInfo);
    }

    private static bool ShouldLogPluginTimingDiagnostics()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_PLUGIN_TIMING_DIAGNOSTICS"))
            || PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS"));
    }
}