using EMMA.Domain;

namespace EMMA.Application.Streaming;

/// <summary>A player's request for part of a stream.</summary>
/// <param name="Path">What follows the host's own prefix: <c>{session}/{part}</c> and, for a directory part, <c>/{rest}</c>.</param>
/// <param name="Range">The bytes asked for, or <see langword="null"/> for all.</param>
/// <param name="Query">The query string, without the <c>?</c>. Only a part beneath a directory passes it on, because a rewritten manifest keeps the one its media addresses carried.</param>
public sealed record StreamRequest(string Path, ByteRange? Range, string? Query = null);
