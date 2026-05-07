using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public static class PluginWasmHostBuilderPresets
{
    public static PluginWasmHostBuilder AddStandardOperations(
        this PluginWasmHostBuilder builder,
        Func<HandshakeResponse> handshake,
        JsonTypeInfo<HandshakeResponse> handshakeTypeInfo,
        Func<CapabilityItem[]> capabilities,
        JsonTypeInfo<CapabilityItem[]> capabilitiesTypeInfo)
    {
        return builder
            .AddCliJson(PluginOperationNames.Handshake, (_, _) => handshake(), handshakeTypeInfo)
            .AddCliJson(PluginOperationNames.Capabilities, (_, _) => capabilities(), capabilitiesTypeInfo);
    }

    public static PluginWasmHostBuilder AddStandardPagedCliOperations(
        this PluginWasmHostBuilder builder,
        Func<string, string, SearchItem[]> search,
        JsonTypeInfo<SearchItem[]> searchTypeInfo,
        Func<string, string, ChapterItem[]> chapters,
        JsonTypeInfo<ChapterItem[]> chaptersTypeInfo,
        Func<string[], string, string> serializePageForCli,
        Func<string[], string, string> serializePagesForCli,
        Func<string[], string, string> serializeInvokeForCli)
    {
        return builder
            .AddCliJson(
                PluginOperationNames.Search,
                (args, payload) => search(args.Length > 0 ? args[0] : string.Empty, payload),
                searchTypeInfo)
            .AddCliJson(
                PluginOperationNames.Chapters,
                (args, payload) => chapters(args.Length > 0 ? args[0] : string.Empty, payload),
                chaptersTypeInfo)
            .AddCliHandler(PluginOperationNames.Page, serializePageForCli)
            .AddCliHandler(PluginOperationNames.Pages, serializePagesForCli)
            .AddCliHandler(PluginOperationNames.Invoke, serializeInvokeForCli);
    }
}