namespace EMMA.Plugin.Common;

/// <summary>
/// Groups the provider-facing SDK collaborators that define a plugin's provider behavior.
/// </summary>
public sealed record PluginProviderBundle<TProviderClient, TQueryEnricher, TSuggestionProvider>(
    TProviderClient Client,
    TQueryEnricher QueryEnricher,
    TSuggestionProvider SuggestionProvider)
    where TProviderClient : class
    where TQueryEnricher : class
    where TSuggestionProvider : class;