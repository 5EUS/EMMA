namespace EMMA.Domain;

/// <summary>
/// One validated item in the media tree: a series, chapter, page, episode, track,
/// recipe or landing section. Nothing about it names a media type.
/// </summary>
/// <param name="Ref">The host-namespaced identity.</param>
/// <param name="Kind">The kind, declared by the owning plugin.</param>
/// <param name="Title">The title.</param>
/// <param name="Parent">The parent node, when known.</param>
/// <param name="Thumbnail">An absolute preview image URL, if any.</param>
/// <param name="Props">Descriptive data such as synopsis, rating, tags and ordinal.</param>
public sealed record Node(
    NodeRef Ref,
    NodeKind Kind,
    string Title,
    NodeRef? Parent,
    Uri? Thumbnail,
    IReadOnlyList<Prop> Props);
