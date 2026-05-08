namespace EMMA.Plugin.Common;

/// <summary>
/// Defines standard operation names used by EMMA plugins.
/// </summary>
public static class PluginOperationNames
{
    /// <summary>
    /// The handshake operation name.
    /// </summary>
    public const string Handshake = "handshake";
    /// <summary>
    /// The capabilities operation name.
    /// </summary>
    public const string Capabilities = "capabilities";
    /// <summary>
    /// The search operation name.
    /// </summary>
    public const string Search = "search";
    /// <summary>
    /// The chapters operation name.
    /// </summary>
    public const string Chapters = "chapters";
    /// <summary>
    /// The page operation name.
    /// </summary>
    public const string Page = "page";
    /// <summary>
    /// The pages operation name.
    /// </summary>
    public const string Pages = "pages";
    /// <summary>
    /// The generic invoke operation name.
    /// </summary>
    public const string Invoke = "invoke";
    /// <summary>
    /// The video streams operation name.
    /// </summary>
    public const string VideoStreams = "video-streams";
    /// <summary>
    /// The video segment operation name.
    /// </summary>
    public const string VideoSegment = "video-segment";
    /// <summary>
    /// The local benchmark operation name.
    /// </summary>
    public const string Benchmark = "benchmark";
    /// <summary>
    /// The network benchmark operation name.
    /// </summary>
    public const string BenchmarkNetwork = "benchmark-network";

    /// <summary>
    /// The standard set of operations recognized by the WASM CLI host.
    /// </summary>
    public static readonly IReadOnlySet<string> WasmCliKnownOperations = new HashSet<string>
    {
        Handshake,
        Capabilities,
        Search,
        Chapters,
        Page,
        Pages,
        Invoke,
        VideoStreams,
        VideoSegment,
        Benchmark,
        BenchmarkNetwork
    };
}

/// <summary>
/// Defines standard EMMA media type names.
/// </summary>
public static class PluginMediaTypes
{
    /// <summary>
    /// The paged media type.
    /// </summary>
    public const string Paged = "paged";
    /// <summary>
    /// The video media type.
    /// </summary>
    public const string Video = "video";
    /// <summary>
    /// The audio media type.
    /// </summary>
    public const string Audio = "audio";
}