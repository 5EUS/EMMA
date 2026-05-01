using System.Text.Json.Serialization;

namespace EMMA.Api.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(ApiErrorEnvelope))]
public partial class ApiErrorJsonContext : JsonSerializerContext
{
}
