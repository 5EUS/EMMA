using System.Text.Json.Serialization;

namespace EMMA.WasmRuntime.Wire;

/// <summary>Source-generated serialization for the host's snake_case JSON.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(WireEnvelope<WireInfo>))]
[JsonSerializable(typeof(WireEnvelope<WireHealth>))]
[JsonSerializable(typeof(WireEnvelope<WireNodePage>))]
[JsonSerializable(typeof(WireEnvelope<IReadOnlyList<WireNode>>))]
[JsonSerializable(typeof(WireEnvelope<IReadOnlyList<WireAsset>>))]
[JsonSerializable(typeof(WireEnvelope<IReadOnlyList<WireChoice>>))]
[JsonSerializable(typeof(WireEnvelope<WireChunkInfo>))]
[JsonSerializable(typeof(WireOpenInfo))]
[JsonSerializable(typeof(WireQuery))]
[JsonSerializable(typeof(WireChildrenRequest))]
[JsonSerializable(typeof(WireIdsRequest))]
[JsonSerializable(typeof(WireResolveRequest))]
[JsonSerializable(typeof(WireReadRequest))]
[JsonSerializable(typeof(WireSuggestRequest))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
