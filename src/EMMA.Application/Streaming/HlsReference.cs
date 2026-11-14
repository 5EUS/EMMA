namespace EMMA.Application.Streaming;

/// <summary>Something an HLS playlist points at.</summary>
/// <param name="Url">The absolute address.</param>
/// <param name="IsPlaylist"><see langword="true"/> for another playlist; <see langword="false"/> for a segment, key or initialization section.</param>
internal readonly record struct HlsReference(Uri Url, bool IsPlaylist);
