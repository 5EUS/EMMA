namespace EMMA.TestSupport;

/// <summary>What a <see cref="StubHttpHandler"/> saw for one request.</summary>
/// <param name="Url">The requested URL.</param>
/// <param name="HeaderNames">The names of the headers sent.</param>
public sealed record RecordedRequest(Uri Url, IReadOnlyList<string> HeaderNames);
