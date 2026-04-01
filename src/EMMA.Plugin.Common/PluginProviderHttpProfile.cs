namespace EMMA.Plugin.Common;

public sealed record PluginProviderHttpProfile(
    Uri BaseUri,
    string UserAgent,
    string AcceptMediaType = "application/json");
