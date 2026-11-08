namespace EMMA.Api.Contracts;

/// <summary>One deliverable resource. Where the bytes come from stays server-side.</summary>
/// <param name="Index">Pass as <c>index</c> to fetch its content.</param>
/// <param name="Mime">The payload type.</param>
/// <param name="Size">The size in bytes, when known.</param>
/// <param name="Delivery"><c>inline</c>, <c>remote</c> or <c>proxied</c>: how the host obtains the bytes.</param>
/// <param name="Props">Typed facts such as quality or language.</param>
internal sealed record AssetDto(
    int Index,
    string Mime,
    long? Size,
    string Delivery,
    IReadOnlyList<PropDto> Props);
