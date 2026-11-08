using System.Text.Json.Serialization;

namespace EMMA.Api.Contracts;

/// <summary>
/// Source-generated JSON for the whole API: no reflection, so it works under NativeAOT. Names are
/// <c>snake_case</c>, absent values are left out, and a request with a field the API does not know is refused.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(PluginSummary))]
[JsonSerializable(typeof(PluginListResponse))]
[JsonSerializable(typeof(ChoiceDto))]
[JsonSerializable(typeof(KindDto))]
[JsonSerializable(typeof(RangeDto))]
[JsonSerializable(typeof(LookupDto))]
[JsonSerializable(typeof(FilterDto))]
[JsonSerializable(typeof(CategoryDto))]
[JsonSerializable(typeof(ConditionDto))]
[JsonSerializable(typeof(SettingDto))]
[JsonSerializable(typeof(PluginInfoDto))]
[JsonSerializable(typeof(PluginDetailResponse))]
[JsonSerializable(typeof(PropDto))]
[JsonSerializable(typeof(NodeDto))]
[JsonSerializable(typeof(FacetCountDto))]
[JsonSerializable(typeof(FacetDto))]
[JsonSerializable(typeof(NodePageResponse))]
[JsonSerializable(typeof(NodeListResponse))]
[JsonSerializable(typeof(FilterValueDto))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(AssetDto))]
[JsonSerializable(typeof(AssetListResponse))]
[JsonSerializable(typeof(SetProgressRequest))]
[JsonSerializable(typeof(ProgressDto))]
[JsonSerializable(typeof(ProgressListResponse))]
[JsonSerializable(typeof(LibraryEntryDto))]
[JsonSerializable(typeof(LibraryResponse))]
public sealed partial class ApiJsonContext : JsonSerializerContext
{
}
