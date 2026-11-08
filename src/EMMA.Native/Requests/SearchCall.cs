using EMMA.Api.Contracts;

namespace EMMA.Native.Requests;

/// <summary>A search in one plugin.</summary>
/// <param name="Plugin">The plugin id.</param>
/// <param name="Query">The query, in the same shape as the HTTP search body.</param>
internal sealed record SearchCall(
    string? Plugin,
    SearchRequest? Query);
