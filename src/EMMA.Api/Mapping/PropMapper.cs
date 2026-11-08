using System.Text.Json.Nodes;

using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps typed properties to their wire form.</summary>
internal static class PropMapper
{
    /// <summary>Maps a list of properties.</summary>
    /// <param name="props">The properties.</param>
    /// <returns>The wire form.</returns>
    public static IReadOnlyList<PropDto> ToDtos(IReadOnlyList<Prop> props) => [.. props.Select(ToDto)];

    private static PropDto ToDto(Prop prop) => new(prop.Key, SnakeCase.Of(prop.Value.Kind), ValueOf(prop.Value));

    private static JsonNode? ValueOf(PropValue value) => value.Kind switch
    {
        PropValueKind.Text or PropValueKind.Url => JsonValue.Create(value.Text),
        PropValueKind.WholeNumber or PropValueKind.Timestamp => JsonValue.Create(value.WholeNumber),

        // JSON has no NaN or infinity; a non-finite number is sent as absent rather than as invalid JSON.
        PropValueKind.Number => double.IsFinite(value.Number) ? JsonValue.Create(value.Number) : null,
        PropValueKind.Flag => JsonValue.Create(value.Flag),
        PropValueKind.TextList => new JsonArray([.. value.TextList.Select(item => (JsonNode?)JsonValue.Create(item))]),
        _ => null,
    };
}
