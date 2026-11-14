namespace EMMA.Api.Contracts;

/// <summary>An opened stream: the address to give a player.</summary>
/// <param name="Url">Where to play it from. Over HTTP this is a path on the API itself and needs the same credentials as any request; over the native library it is an address on this device that needs none.</param>
/// <param name="Mime">The type of what the address serves first: the stream's manifest or file.</param>
public sealed record StreamOpenResponse(Uri Url, string Mime);
