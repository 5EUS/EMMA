namespace EMMA.Api.Contracts;

/// <summary>One item in the media tree.</summary>
/// <param name="Ref">The canonical <c>plugin:id</c> reference.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Title">The title.</param>
/// <param name="Parent">The parent reference, when known.</param>
/// <param name="Thumbnail">An absolute preview image URL.</param>
/// <param name="Props">Descriptive data.</param>
internal sealed record NodeDto(
    string Ref,
    string Kind,
    string Title,
    string? Parent,
    string? Thumbnail,
    IReadOnlyList<PropDto> Props);
