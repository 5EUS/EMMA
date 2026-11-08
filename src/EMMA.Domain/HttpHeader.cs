namespace EMMA.Domain;

/// <summary>An HTTP header.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The header value.</param>
public sealed record HttpHeader(string Name, string Value);
