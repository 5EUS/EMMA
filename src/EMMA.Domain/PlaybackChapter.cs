namespace EMMA.Domain;

/// <summary>A chapter marker inside a playable node.</summary>
/// <param name="Index">Its position among the node's markers.</param>
/// <param name="StartMs">Where it starts, in milliseconds.</param>
/// <param name="EndMs">Where it ends, when the plugin said.</param>
/// <param name="Title">The name to show.</param>
public sealed record PlaybackChapter(int Index, long StartMs, long? EndMs, string Title);
