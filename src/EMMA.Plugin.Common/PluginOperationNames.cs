namespace EMMA.Plugin.Common;

public static class PluginOperationNames
{
    public static readonly IReadOnlySet<string> WasmCliKnownOperations = new HashSet<string>
    {
        "handshake",
        "capabilities",
        "search",
        "chapters",
        "page",
        "pages",
        "invoke",
        "benchmark",
        "benchmark-network"
    };
}