using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(List<string>))]
internal partial class PluginPreferenceValueJsonContext : JsonSerializerContext
{
}