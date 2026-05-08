using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Adds standard operation sets to a <see cref="PluginWasmHostBuilder"/>.
/// </summary>
public static class PluginWasmHostBuilderPresets
{
    /// <summary>
    /// Adds the standard handshake and capability CLI operations to a WASM host builder.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="handshake">The handshake response factory.</param>
    /// <param name="handshakeTypeInfo">The JSON type metadata for the handshake response.</param>
    /// <param name="capabilities">The capability response factory.</param>
    /// <param name="capabilitiesTypeInfo">The JSON type metadata for the capability response.</param>
    /// <returns>The configured builder.</returns>
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

    /// <summary>
    /// Adds the standard paged-media CLI operations to a WASM host builder.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="search">The search handler.</param>
    /// <param name="searchTypeInfo">The JSON type metadata for search results.</param>
    /// <param name="chapters">The chapters handler.</param>
    /// <param name="chaptersTypeInfo">The JSON type metadata for chapter results.</param>
    /// <param name="serializePageForCli">The page serialization handler.</param>
    /// <param name="serializePagesForCli">The pages serialization handler.</param>
    /// <param name="serializeInvokeForCli">The invoke serialization handler.</param>
    /// <returns>The configured builder.</returns>
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