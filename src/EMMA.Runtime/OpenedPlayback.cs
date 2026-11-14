namespace EMMA.Runtime;

/// <summary>An asset opened for playing: where a player on this machine asks for it.</summary>
/// <param name="Url">The address on this machine.</param>
/// <param name="Mime">The type of what the address serves first: the stream's manifest or file.</param>
public sealed record OpenedPlayback(Uri Url, string Mime);
