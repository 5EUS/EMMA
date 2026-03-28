namespace EMMA.Plugin.Common;

public static class PluginOperationNames
{
    public const string Handshake = "handshake";
    public const string Capabilities = "capabilities";
    public const string Search = "search";
    public const string Chapters = "chapters";
    public const string Page = "page";
    public const string Pages = "pages";
    public const string Invoke = "invoke";
    public const string VideoStreams = "video-streams";
    public const string VideoSegment = "video-segment";
    public const string Benchmark = "benchmark";
    public const string BenchmarkNetwork = "benchmark-network";

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

public static class PluginMediaTypes
{
    public const string Paged = "paged";
    public const string Video = "video";
    public const string Audio = "audio";
}