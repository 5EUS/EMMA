using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(PipelineErrorEnvelope))]
internal partial class PipelineErrorJsonContext : JsonSerializerContext
{
}
