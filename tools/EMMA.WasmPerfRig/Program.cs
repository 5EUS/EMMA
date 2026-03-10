using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using EMMA.PluginHost.Library;
using EMMA.PluginHost.Services;

return args.Length == 0
    ? ExitWithUsage("Missing mode. Use 'app-flow' or 'direct-runtime'.")
    : await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var mode = args[0].Trim().ToLowerInvariant();
        var options = CliOptions.Parse(args.Skip(1).ToArray());
        EnsureNativeRuntimeLoadConfigured(options);

        return mode switch
        {
            "app-flow" => await RunAppFlowAsync(options),
            "direct-runtime" => RunDirectRuntime(options),
            _ => ExitWithUsage($"Unknown mode '{mode}'.")
        };
    }
    catch (InvalidOperationException ex)
    {
        return ExitWithUsage(ex.Message);
    }
}

static async Task<int> RunAppFlowAsync(CliOptions options)
{
    var pluginId = options.Require("plugin-id");
    var operation = options.Get("op", "search").Trim().ToLowerInvariant();
    var appSupportOverride = options.Get("app-support-dir", string.Empty);
    var defaultDirs = string.IsNullOrWhiteSpace(appSupportOverride)
        ? ResolveFrontendLikeDirectories(pluginId)
        : ResolveDirectoriesFromSupportRoot(appSupportOverride);
    var manifestsDir = options.Get("manifests", defaultDirs.ManifestsPath);
    var sandboxDir = options.Get("sandbox", options.Get("plugins", defaultDirs.PluginsPath));
    var query = options.Get("query", string.Empty);
    var cacheBust = options.GetBool("cache-bust", false);
    var iterations = options.GetInt("iterations", 5, min: 1, max: 500);
    var warmup = options.GetInt("warmup", 1, min: 0, max: 50);
    var hasInMemoryToggle = options.Has("in-memory-bridge");
    var inMemoryBridge = options.GetBool("in-memory-bridge", false);
    var hasInMemoryMaxBytes = options.Has("in-memory-bridge-max-bytes");
    var inMemoryBridgeMaxBytes = options.GetInt("in-memory-bridge-max-bytes", 262_144, min: 1, max: 16_000_000);
    var hasDirectHttpToggle = options.Has("direct-http");
    var directHttp = options.GetBool("direct-http", false);

    EnsureDirectoryExists(manifestsDir);
    EnsureDirectoryExists(sandboxDir);

    if (!Directory.Exists(manifestsDir))
    {
        return Fail($"manifests directory not found: {manifestsDir}");
    }

    if (!Directory.Exists(sandboxDir))
    {
        return Fail($"sandbox directory not found: {sandboxDir}");
    }

    Console.WriteLine("mode=app-flow");
    Console.WriteLine($"frontendDefaultManifests={defaultDirs.ManifestsPath}");
    Console.WriteLine($"frontendDefaultPlugins={defaultDirs.PluginsPath}");
    Console.WriteLine($"manifests={manifestsDir}");
    Console.WriteLine($"sandbox={sandboxDir}");
    Console.WriteLine($"pluginId={pluginId}");
    Console.WriteLine($"op={operation}");
    Console.WriteLine($"query={query}");
    Console.WriteLine($"cacheBust={cacheBust}");
    Console.WriteLine($"warmup={warmup} iterations={iterations}");

    if (hasInMemoryToggle)
    {
        Environment.SetEnvironmentVariable("EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD", inMemoryBridge ? "1" : "0");
    }

    if (hasInMemoryMaxBytes)
    {
        Environment.SetEnvironmentVariable("EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD_MAX_BYTES", inMemoryBridgeMaxBytes.ToString());
    }

    if (hasDirectHttpToggle)
    {
        Environment.SetEnvironmentVariable("EMMA_WASM_DIRECT_HTTP", directHttp ? "1" : "0");
    }

    Console.WriteLine($"inMemoryBridge={Environment.GetEnvironmentVariable("EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD") ?? "<default>"}");
    Console.WriteLine($"inMemoryBridgeMaxBytes={Environment.GetEnvironmentVariable("EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD_MAX_BYTES") ?? "<default>"}");
    Console.WriteLine($"directHttp={Environment.GetEnvironmentVariable("EMMA_WASM_DIRECT_HTTP") ?? "<default>"}");

    var init = PluginHostExports.InitializeManaged(manifestsDir, sandboxDir);
    if (init != 0)
    {
        return Fail($"initialize failed: {PluginHostExports.GetLastErrorManaged() ?? "<none>"}");
    }

    try
    {
        for (var i = 0; i < warmup; i++)
        {
            _ = RunAppFlowOperation(options, pluginId, operation, query, i + 1, cacheBust);
            _ = PluginHostExports.TakeLastWasmNativeTimingManaged();
        }

        var rows = new List<SampleRow>(iterations);
        for (var i = 1; i <= iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var json = RunAppFlowOperation(options, pluginId, operation, query, i, cacheBust);
            sw.Stop();

            var err = PluginHostExports.GetLastErrorManaged();
            var nativeTiming = PluginHostExports.TakeLastWasmNativeTimingManaged();
            var nativeTotalMs = TryExtractNativeTotalMs(nativeTiming);
            long? hostOverheadMs = nativeTotalMs is > 0
                ? Math.Max(0, sw.ElapsedMilliseconds - nativeTotalMs.Value)
                : null;
            var count = TryGetResultCount(json);

            rows.Add(new SampleRow(
                i,
                sw.ElapsedMilliseconds,
                Ok: json is not null,
                count,
                err,
                nativeTiming,
                nativeTotalMs,
                hostOverheadMs));
        }

        PrintRows(rows);
        PrintSummary(rows);
        return 0;
    }
    finally
    {
        PluginHostExports.ShutdownManaged();
    }
}

static int RunDirectRuntime(CliOptions options)
{
    var component = options.Require("component");
    var operation = options.Require("operation");
    var timeoutMs = (uint)options.GetInt("timeout-ms", 8_000, min: 1, max: 120_000);
    var iterations = options.GetInt("iterations", 5, min: 1, max: 500);
    var warmup = options.GetInt("warmup", 1, min: 0, max: 50);
    var opArgs = options.GetMany("op-arg");

    if (!File.Exists(component))
    {
        return Fail($"component not found: {component}");
    }

    Console.WriteLine("mode=direct-runtime");
    Console.WriteLine($"component={component}");
    Console.WriteLine($"operation={operation}");
    Console.WriteLine($"timeoutMs={timeoutMs}");
    Console.WriteLine($"opArgs=[{string.Join(",", opArgs)}]");
    Console.WriteLine($"warmup={warmup} iterations={iterations}");

    for (var i = 0; i < warmup; i++)
    {
        _ = NativeRuntimeInvoke(component, operation, opArgs, timeoutMs);
        _ = NativeRuntimeBindings.TakeLastTiming();
    }

    var rows = new List<SampleRow>(iterations);
    for (var i = 1; i <= iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        var result = NativeRuntimeInvoke(component, operation, opArgs, timeoutMs);
        sw.Stop();

        var nativeTiming = NativeRuntimeBindings.TakeLastTiming();
        var nativeTotalMs = TryExtractNativeTotalMs(nativeTiming);
        var err = result.Error;
        var count = TryGetResultCount(result.Json);

        rows.Add(new SampleRow(
            i,
            sw.ElapsedMilliseconds,
            Ok: result.Success,
            count,
            err,
            nativeTiming,
            nativeTotalMs,
            HostOverheadMs: null));
    }

    PrintRows(rows);
    PrintSummary(rows);
    return 0;
}

static string? RunAppFlowOperation(
    CliOptions options,
    string pluginId,
    string operation,
    string query,
    int iteration,
    bool cacheBust)
{
    switch (operation)
    {
        case "search":
        {
            var finalQuery = cacheBust
                ? $"{query} __rig_{iteration}_{Guid.NewGuid():N}" 
                : query;
            return PluginHostExports.SearchJsonManaged(pluginId, finalQuery);
        }
        case "chapters":
        {
            var mediaId = options.Require("media-id");
            return PluginHostExports.GetChaptersJsonManaged(pluginId, mediaId);
        }
        case "page":
        {
            var mediaId = options.Require("media-id");
            var chapterId = options.Require("chapter-id");
            var pageIndex = options.GetInt("page-index", 0, min: 0, max: int.MaxValue);
            return PluginHostExports.GetPageJsonManaged(pluginId, mediaId, chapterId, pageIndex);
        }
        case "pages":
        {
            var mediaId = options.Require("media-id");
            var chapterId = options.Require("chapter-id");
            var startIndex = options.GetInt("start-index", 0, min: 0, max: int.MaxValue);
            var count = options.GetInt("count", 24, min: 1, max: 500);
            return PluginHostExports.GetPagesJsonManaged(pluginId, mediaId, chapterId, startIndex, count);
        }
        default:
            throw new InvalidOperationException($"Unsupported app-flow operation '{operation}'. Use search|chapters|page|pages.");
    }
}

static NativeInvokeResult NativeRuntimeInvoke(string component, string operation, IReadOnlyList<string> opArgs, uint timeoutMs)
{
    var argsJson = JsonSerializer.Serialize(opArgs);
    var componentPtr = Marshal.StringToCoTaskMemUTF8(component);
    var operationPtr = Marshal.StringToCoTaskMemUTF8(operation);
    var argsPtr = Marshal.StringToCoTaskMemUTF8(argsJson);

    try
    {
        var code = NativeRuntimeBindings.Invoke(componentPtr, operationPtr, argsPtr, timeoutMs, out var outJson, out var outErr);
        try
        {
            var json = PtrToString(outJson);
            var err = PtrToString(outErr);
            return code == 0
                ? new NativeInvokeResult(true, json, null)
                : new NativeInvokeResult(false, json, string.IsNullOrWhiteSpace(err) ? $"native error code={code}" : err);
        }
        finally
        {
            NativeRuntimeBindings.FreeString(outJson);
            NativeRuntimeBindings.FreeString(outErr);
        }
    }
    catch (Exception ex)
    {
        return new NativeInvokeResult(false, null, ex.ToString());
    }
    finally
    {
        Marshal.FreeCoTaskMem(componentPtr);
        Marshal.FreeCoTaskMem(operationPtr);
        Marshal.FreeCoTaskMem(argsPtr);
    }
}

static string? PtrToString(IntPtr ptr)
{
    return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
}

static int TryGetResultCount(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return 0;
    }

    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.GetArrayLength();
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("pages", out var pages)
                && pages.ValueKind == JsonValueKind.Array)
            {
                return pages.GetArrayLength();
            }

            if (doc.RootElement.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                return results.GetArrayLength();
            }
        }
    }
    catch
    {
    }

    return 0;
}

static long? TryExtractNativeTotalMs(string? nativeTiming)
{
    if (string.IsNullOrWhiteSpace(nativeTiming))
    {
        return null;
    }

    var match = Regex.Match(nativeTiming, @"\btotalMs=(\d+)");
    if (!match.Success)
    {
        return null;
    }

    return long.TryParse(match.Groups[1].Value, out var value)
        ? value
        : null;
}

static void PrintRows(IReadOnlyList<SampleRow> rows)
{
    foreach (var row in rows)
    {
        var err = string.IsNullOrWhiteSpace(row.Error) ? "<none>" : row.Error.Replace('\n', ' ').Replace('\r', ' ');
        var nativeTotalText = row.NativeTotalMs?.ToString() ?? "n/a";
        var hostOverheadText = row.HostOverheadMs?.ToString() ?? "n/a";
        Console.WriteLine($"iter={row.Iteration} ms={row.ElapsedMs} nativeTotalMs={nativeTotalText} hostOverheadMs={hostOverheadText} ok={row.Ok} count={row.ResultCount} err={err}");
        if (!string.IsNullOrWhiteSpace(row.NativeTiming))
        {
            Console.WriteLine($"nativeTiming={row.NativeTiming}");
        }
    }
}

static void PrintSummary(IReadOnlyList<SampleRow> rows)
{
    if (rows.Count == 0)
    {
        return;
    }

    var elapsed = rows.Select(r => r.ElapsedMs).OrderBy(v => v).ToArray();
    var success = rows.Count(r => r.Ok);
    var failed = rows.Count - success;
    var avg = elapsed.Average();
    var p50 = Percentile(elapsed, 0.50);
    var p95 = Percentile(elapsed, 0.95);
    var max = elapsed[^1];
    var hostOverhead = rows.Where(r => r.HostOverheadMs.HasValue).Select(r => r.HostOverheadMs!.Value).ToArray();
    var avgHostOverhead = hostOverhead.Length > 0 ? hostOverhead.Average() : double.NaN;

    Console.WriteLine(
        $"summary success={success} failed={failed} avgMs={avg:F1} p50Ms={p50} p95Ms={p95} maxMs={max} avgHostOverheadMs={(double.IsNaN(avgHostOverhead) ? "n/a" : avgHostOverhead.ToString("F1"))}");
}

static long Percentile(long[] sorted, double percentile)
{
    if (sorted.Length == 0)
    {
        return 0;
    }

    var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    if (index < 0)
    {
        index = 0;
    }
    if (index >= sorted.Length)
    {
        index = sorted.Length - 1;
    }

    return sorted[index];
}

static int ExitWithUsage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run -- app-flow --plugin-id <id> --op <search|chapters|page|pages> [--query <q>] [--media-id <id>] [--chapter-id <id>] [--page-index N] [--start-index N] [--count N] [--cache-bust true|false] [--direct-http true|false] [--in-memory-bridge true|false] [--in-memory-bridge-max-bytes N] [--runtime-lib-dir <dir>] [--app-support-dir <dir>] [--manifests <dir>] [--sandbox <dir>|--plugins <dir>] [--iterations N] [--warmup N]");
    Console.Error.WriteLine("  dotnet run -- direct-runtime --component <plugin.wasm> --operation <op> [--runtime-lib-dir <dir>] [--op-arg v]... [--timeout-ms N] [--iterations N] [--warmup N]");
    return 2;
}

static void EnsureNativeRuntimeLoadConfigured(CliOptions options)
{
    var runtimeLibDir = options.Get("runtime-lib-dir", string.Empty);
    if (string.IsNullOrWhiteSpace(runtimeLibDir))
    {
        runtimeLibDir = ResolveDefaultRuntimeLibDirectory();
    }

    if (string.IsNullOrWhiteSpace(runtimeLibDir) || !Directory.Exists(runtimeLibDir))
    {
        Console.WriteLine("nativeRuntimeLibDir=<not-found>");
        return;
    }

    var nativeLibraryPath = Path.Combine(runtimeLibDir, "libemma_wasm_runtime.dylib");
    if (!File.Exists(nativeLibraryPath))
    {
        Console.WriteLine($"nativeRuntimeLibDir={runtimeLibDir} (missing libemma_wasm_runtime.dylib)");
        return;
    }

    TrySetResolver(typeof(NativeRuntimeBindings).Assembly, nativeLibraryPath);
    TrySetResolver(typeof(NativeInProcessWasmComponentInvoker).Assembly, nativeLibraryPath);

    Console.WriteLine($"nativeRuntimeLib={nativeLibraryPath}");
}

static string ResolveDefaultRuntimeLibDirectory()
{
    var explicitDir = Environment.GetEnvironmentVariable("EMMA_WASM_RUNTIME_LIB_DIR");
    if (!string.IsNullOrWhiteSpace(explicitDir))
    {
        return explicitDir;
    }

    var cwdCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "artifacts", "wasm-runtime-native", "osx-arm64"));
    if (Directory.Exists(cwdCandidate))
    {
        return cwdCandidate;
    }

    var repoRoot = FindRepositoryRoot();
    if (!string.IsNullOrWhiteSpace(repoRoot))
    {
        var repoCandidate = Path.Combine(repoRoot, "artifacts", "wasm-runtime-native", "osx-arm64");
        if (Directory.Exists(repoCandidate))
        {
            return repoCandidate;
        }
    }

    return string.Empty;
}

static string? FindRepositoryRoot()
{
    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "EMMA.sln")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return null;
}

static void TrySetResolver(Assembly assembly, string nativeLibraryPath)
{
    try
    {
        NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, searchPath) =>
        {
            if (!string.Equals(libraryName, "emma_wasm_runtime", StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            if (NativeLibrary.TryLoad(nativeLibraryPath, out var handle))
            {
                return handle;
            }

            return IntPtr.Zero;
        });
    }
    catch (InvalidOperationException)
    {
    }
}

static FrontendDirectories ResolveDirectoriesFromSupportRoot(string supportRoot)
{
    var root = Path.Combine(supportRoot, "emmaui");
    return new FrontendDirectories(
        Path.Combine(root, "manifests"),
        Path.Combine(root, "plugins"));
}

static FrontendDirectories ResolveFrontendLikeDirectories(string? pluginId)
{
    var supportCandidates = new List<string>();

    AddIfNotEmpty(supportCandidates, Environment.GetEnvironmentVariable("EMMA_APP_SUPPORT_DIR"));

    var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
    if (!string.IsNullOrWhiteSpace(home))
    {
        AddIfNotEmpty(supportCandidates, Path.Combine(home, "Library", "Containers", "com.example.emmaui", "Data", "Library", "Application Support"));
        AddIfNotEmpty(supportCandidates, Path.Combine(home, "Library", "Containers", "com.example.emmaui", "Data", "Library", "Application Support", "com.example.emmaui"));
        AddIfNotEmpty(supportCandidates, Path.Combine(home, "Library", "Application Support"));
    }

    AddIfNotEmpty(supportCandidates, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    var distinct = supportCandidates
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    foreach (var supportDir in distinct)
    {
        if (!Directory.Exists(supportDir))
        {
            continue;
        }

        var root = Path.Combine(supportDir, "emmaui");
        var candidate = new FrontendDirectories(
            Path.Combine(root, "manifests"),
            Path.Combine(root, "plugins"));

        if (ContainsPlugin(candidate, pluginId))
        {
            return candidate;
        }

        return candidate;
    }

    var fallbackSupport = distinct.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(fallbackSupport))
    {
        throw new InvalidOperationException("Unable to resolve application support directory for frontend-like path resolution.");
    }

    var fallbackRoot = Path.Combine(fallbackSupport, "emmaui");
    return new FrontendDirectories(
        Path.Combine(fallbackRoot, "manifests"),
        Path.Combine(fallbackRoot, "plugins"));
}

static bool ContainsPlugin(FrontendDirectories dirs, string? pluginId)
{
    if (string.IsNullOrWhiteSpace(pluginId))
    {
        return false;
    }

    var manifestPath = Path.Combine(dirs.ManifestsPath, pluginId + ".json");
    var pluginPath = Path.Combine(dirs.PluginsPath, pluginId);
    return File.Exists(manifestPath) || Directory.Exists(pluginPath);
}

static void EnsureDirectoryExists(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    Directory.CreateDirectory(path);
}

static void AddIfNotEmpty(ICollection<string> values, string? path)
{
    if (!string.IsNullOrWhiteSpace(path))
    {
        values.Add(path);
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

sealed record SampleRow(
    int Iteration,
    long ElapsedMs,
    bool Ok,
    int ResultCount,
    string? Error,
    string? NativeTiming,
    long? NativeTotalMs,
    long? HostOverheadMs);

sealed record NativeInvokeResult(bool Success, string? Json, string? Error);
sealed record FrontendDirectories(string ManifestsPath, string PluginsPath);

sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> _values;

    private CliOptions(Dictionary<string, List<string>> values)
    {
        _values = values;
    }

    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";

            if (!values.TryGetValue(key, out var bucket))
            {
                bucket = [];
                values[key] = bucket;
            }

            bucket.Add(value);
        }

        return new CliOptions(values);
    }

    public string Require(string key)
    {
        if (!TryGetFirst(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option --{key}");
        }

        return value;
    }

    public string Get(string key, string fallback)
    {
        return TryGetFirst(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    public IReadOnlyList<string> GetMany(string key)
    {
        return _values.TryGetValue(key, out var values)
            ? values
            : [];
    }

    public int GetInt(string key, int fallback, int min, int max)
    {
        var raw = Get(key, fallback.ToString());
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key, fallback ? "true" : "false");
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        return raw switch
        {
            "1" => true,
            "0" => false,
            _ => fallback
        };
    }

    public bool Has(string key)
    {
        return _values.ContainsKey(key);
    }

    private bool TryGetFirst(string key, out string? value)
    {
        value = null;
        if (!_values.TryGetValue(key, out var values) || values.Count == 0)
        {
            return false;
        }

        value = values[0];
        return true;
    }
}

static class NativeRuntimeBindings
{
    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_component_invoke", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Invoke(
        IntPtr componentPath,
        IntPtr operation,
        IntPtr operationArgsJson,
        uint timeoutMs,
        out IntPtr outJson,
        out IntPtr outError);

    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FreeString(IntPtr value);

    [DllImport("emma_wasm_runtime", EntryPoint = "emma_wasm_runtime_take_last_timing", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr TakeLastTimingPtr();

    public static string? TakeLastTiming()
    {
        try
        {
            var ptr = TakeLastTimingPtr();
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                FreeString(ptr);
            }
        }
        catch
        {
            return null;
        }
    }
}