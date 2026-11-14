namespace EMMA.Application.Streaming;

/// <summary>An opened stream: where to ask for it.</summary>
/// <param name="Session">The unguessable name of the session.</param>
/// <param name="Path">The path of the stream's first part, <c>{session}/0</c>; a host puts its own address in front.</param>
/// <param name="Mime">The type of the first part.</param>
public sealed record StreamLink(string Session, string Path, string Mime);
