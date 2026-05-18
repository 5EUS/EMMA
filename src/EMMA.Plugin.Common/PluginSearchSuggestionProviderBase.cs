namespace EMMA.Plugin.Common;

/// <summary>
/// Base class for lookup-backed search suggestion providers.
/// </summary>
public abstract class PluginSearchSuggestionProviderBase
{
    /// <summary>
    /// Resolves suggestions asynchronously by using a shared payload source.
    /// </summary>
    public Task<IReadOnlyList<SearchSuggestionItem>> GetSuggestionsAsync(
        SearchSuggestionRequest request,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadSource);

        return GetSuggestionsCoreAsync(
            request,
            payloadSource,
            ClampLimit(request.Limit),
            GetExistingValues(request.SearchQuery, request.ControlId),
            cancellationToken);
    }

    /// <summary>
    /// Resolves suggestions synchronously by using a shared payload source.
    /// </summary>
    public IReadOnlyList<SearchSuggestionItem> GetSuggestions(
        SearchSuggestionRequest request,
        PluginPayloadSource payloadSource)
    {
        return GetSuggestionsAsync(request, payloadSource, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Provider-specific suggestion resolution entry point.
    /// </summary>
    protected abstract Task<IReadOnlyList<SearchSuggestionItem>> GetSuggestionsCoreAsync(
        SearchSuggestionRequest request,
        PluginPayloadSource payloadSource,
        int limit,
        HashSet<string> excludedValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clamps a requested suggestion limit to a safe range.
    /// </summary>
    protected static int ClampLimit(int? requestedLimit, int defaultLimit = 20, int minLimit = 1, int maxLimit = 50)
    {
        return Math.Clamp(requestedLimit ?? defaultLimit, minLimit, maxLimit);
    }

    /// <summary>
    /// Collects existing values from the current search query so duplicates can be filtered out.
    /// </summary>
    protected static HashSet<string> GetExistingValues(PluginSearchQuery? searchQuery, string controlId)
    {
        return searchQuery?.GetFilterValues(controlId) is { Count: > 0 } existing
            ? new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters and ranks lookup-backed suggestions from a name lookup.
    /// </summary>
    protected static IReadOnlyList<SearchSuggestionItem> FilterLookupSuggestions(
        IReadOnlyDictionary<string, string> lookup,
        string input,
        int limit,
        HashSet<string> excludedValues)
    {
        if (lookup.Count == 0)
        {
            return [];
        }

        var needle = input?.Trim() ?? string.Empty;

        return [.. lookup
            .OrderBy(entry => RankSuggestion(entry.Key, needle))
            .ThenBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Where(entry => string.IsNullOrWhiteSpace(needle)
                || entry.Key.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !excludedValues.Contains(entry.Key))
            .Take(limit)
            .Select(static entry => new SearchSuggestionItem(
                entry.Key,
                entry.Key,
                entry.Value))];
    }

    /// <summary>
    /// Ranks a suggestion candidate relative to the user input.
    /// </summary>
    protected static int RankSuggestion(string candidate, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return 2;
        }

        if (string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }
}