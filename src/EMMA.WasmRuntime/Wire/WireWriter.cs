using EMMA.Domain;

namespace EMMA.WasmRuntime.Wire;

/// <summary>Turns Domain requests into the shapes the host reads.</summary>
internal static class WireWriter
{
    /// <summary>Maps a search request.</summary>
    /// <param name="query">The Domain form.</param>
    /// <returns>The wire form.</returns>
    internal static WireQuery Query(Query query) => new(
        query.Text,
        [.. query.Kinds.Select(kind => kind.Value)],
        [.. query.Filters.Select(filter => new WireFilterValue(filter.Id, filter.Values))],
        query.Sort,
        query.Cursor,
        Limit(query.Limit));

    /// <summary>Maps a children request.</summary>
    /// <param name="parent">The parent id, or the root.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <returns>The wire form.</returns>
    internal static WireChildrenRequest Children(LocalNodeId? parent, PageRequest request) =>
        new(parent?.Value, new WirePageRequest(request.Cursor, Limit(request.Limit)));

    /// <summary>Maps a resolve request.</summary>
    /// <param name="request">The Domain form.</param>
    /// <returns>The wire form.</returns>
    internal static WireResolveRequest Resolve(ResolveRequest request) =>
        new(request.Node.Value, request.Role.Value, [.. request.Hints.Select(Prop)]);

    /// <summary>Maps a read request.</summary>
    /// <param name="token">The proxied asset's token.</param>
    /// <param name="range">The range, or everything.</param>
    /// <returns>The wire form.</returns>
    internal static WireReadRequest Read(string token, ByteRange? range)
    {
        if (range is null)
        {
            return new WireReadRequest(token, null);
        }

        ulong? length = range.Length is { } value ? ToUnsigned(value) : null;
        return new WireReadRequest(token, new WireByteRange(ToUnsigned(range.Start), length));
    }

    private static WireProp Prop(Prop prop) => new(prop.Key, PropValue(prop.Value));

    private static WirePropValue PropValue(PropValue value) => value.Kind switch
    {
        PropValueKind.Text => new WirePropValue(value.Text, null, null, null, null, null, null),
        PropValueKind.WholeNumber => new WirePropValue(null, value.WholeNumber, null, null, null, null, null),
        PropValueKind.Number => new WirePropValue(null, null, value.Number, null, null, null, null),
        PropValueKind.Flag => new WirePropValue(null, null, null, value.Flag, null, null, null),
        PropValueKind.Timestamp => new WirePropValue(null, null, null, null, value.WholeNumber, null, null),
        PropValueKind.Url => new WirePropValue(null, null, null, null, null, value.Text, null),
        PropValueKind.TextList => new WirePropValue(null, null, null, null, null, null, value.TextList),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown property value kind."),
    };

    private static uint? Limit(int? limit) => limit is { } value ? (uint)Math.Max(value, 0) : null;

    private static ulong ToUnsigned(long value) => (ulong)Math.Max(value, 0);
}
