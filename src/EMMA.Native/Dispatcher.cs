using System.Collections.Frozen;

using EMMA.Domain;
using EMMA.Native.Operations;

namespace EMMA.Native;

/// <summary>
/// Maps an operation name to its handler. The ABI has one call, <c>emma_call</c>; a new capability is a new
/// name here, so the C surface never grows and never needs a second version of an export.
/// </summary>
internal static class Dispatcher
{
    private static readonly FrozenDictionary<string, Func<OperationContext, Task<NativeResponse>>> Handlers =
        new Dictionary<string, Func<OperationContext, Task<NativeResponse>>>(StringComparer.Ordinal)
        {
            ["plugins.list"] = PluginOperations.ListAsync,
            ["plugins.get"] = PluginOperations.GetAsync,
            ["plugins.settings_get"] = PluginOperations.SettingsGetAsync,
            ["plugins.settings_set"] = PluginOperations.SettingsSetAsync,
            ["plugins.install"] = PluginOperations.InstallAsync,
            ["plugins.set_status"] = PluginOperations.SetStatusAsync,
            ["plugins.uninstall"] = PluginOperations.UninstallAsync,
            ["catalog.search"] = CatalogOperations.SearchAsync,
            ["catalog.browse"] = CatalogOperations.BrowseAsync,
            ["catalog.children"] = CatalogOperations.ChildrenAsync,
            ["catalog.get"] = CatalogOperations.GetAsync,
            ["assets.list"] = AssetOperations.ListAsync,
            ["assets.content"] = AssetOperations.ContentAsync,
            ["progress.set"] = UserOperations.SetProgressAsync,
            ["progress.get"] = UserOperations.GetProgressAsync,
            ["progress.continue"] = UserOperations.ContinueAsync,
            ["progress.recent"] = UserOperations.RecentAsync,
            ["progress.history"] = UserOperations.HistoryAsync,
            ["progress.series"] = UserOperations.SeriesAsync,
            ["progress.delete"] = UserOperations.DeleteAsync,
            ["progress.clear"] = UserOperations.ClearAsync,
            ["progress.complete"] = UserOperations.CompleteAsync,
            ["progress.retention_get"] = UserOperations.RetentionGetAsync,
            ["progress.retention_set"] = UserOperations.RetentionSetAsync,
            ["library.list"] = LibraryOperations.ListAsync,
            ["library.search"] = LibraryOperations.SearchAsync,
            ["library.add"] = LibraryOperations.AddAsync,
            ["library.remove"] = LibraryOperations.RemoveAsync,
            ["library.move"] = LibraryOperations.MoveAsync,
            ["library.libraries"] = LibraryOperations.LibrariesAsync,
            ["library.create"] = LibraryOperations.CreateAsync,
            ["library.delete"] = LibraryOperations.DeleteAsync,
            ["app_settings.get"] = AppSettingsOperations.GetAsync,
            ["app_settings.set"] = AppSettingsOperations.SetAsync,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets every operation name, for documentation and tests.</summary>
    public static IReadOnlyCollection<string> Operations => Handlers.Keys;

    /// <summary>Runs an operation.</summary>
    /// <param name="operation">The operation name, such as <c>catalog.search</c>.</param>
    /// <param name="context">The call.</param>
    /// <returns>The response; an unknown operation is an <c>unsupported</c> error, not an exception.</returns>
    public static Task<NativeResponse> InvokeAsync(string operation, OperationContext context) =>
        Handlers.TryGetValue(operation, out Func<OperationContext, Task<NativeResponse>>? handler)
            ? handler(context)
            : Task.FromResult(Envelope.Failure(ErrorCode.Unsupported, $"Unknown operation '{operation}'."));
}
