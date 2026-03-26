using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Services;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginRepositoryStateFile))]
[JsonSerializable(typeof(PluginRepositoryRecord))]
[JsonSerializable(typeof(List<PluginRepositoryRecord>))]
[JsonSerializable(typeof(IReadOnlyList<PluginRepositoryRecord>))]
[JsonSerializable(typeof(PluginRepositoryCatalog))]
[JsonSerializable(typeof(PluginRepositoryCatalogPlugin))]
[JsonSerializable(typeof(PluginRepositoryCatalogRelease))]
[JsonSerializable(typeof(List<PluginRepositoryCatalogPlugin>))]
[JsonSerializable(typeof(List<PluginRepositoryCatalogRelease>))]
[JsonSerializable(typeof(RepositoryPluginsResponse))]
[JsonSerializable(typeof(PluginRepositoryPluginView))]
[JsonSerializable(typeof(PluginRepositoryReleaseView))]
[JsonSerializable(typeof(List<PluginRepositoryPluginView>))]
[JsonSerializable(typeof(List<PluginRepositoryReleaseView>))]
[JsonSerializable(typeof(IReadOnlyList<PluginRepositoryPluginView>))]
[JsonSerializable(typeof(IReadOnlyList<PluginRepositoryReleaseView>))]
[JsonSerializable(typeof(PluginRepositoryInstallResult))]
public partial class PluginRepositoryJsonContext : JsonSerializerContext
{
}
