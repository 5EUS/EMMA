namespace EMMA.Application.Streaming;

/// <summary>How a part of a stream session is served.</summary>
internal enum StreamPartKind
{
    /// <summary>A direct file, such as an <c>.mp4</c>: passed through with ranges, never cached.</summary>
    File,

    /// <summary>A playlist or manifest: fetched whole, rewritten, and remembered for repair.</summary>
    Manifest,

    /// <summary>One small piece of media (a segment, key or initialization section): fetched whole and cached.</summary>
    Segment,

    /// <summary>A directory a DASH manifest was pointed at: anything beneath it is a segment.</summary>
    Directory,
}
