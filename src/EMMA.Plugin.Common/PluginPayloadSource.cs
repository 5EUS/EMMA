namespace EMMA.Plugin.Common;

/// <summary>
/// Wraps provider payload fetching behind a single sync/async-capable abstraction.
/// </summary>
public sealed class PluginPayloadSource
{
    private readonly Func<string, string?>? _syncFetch;
    private readonly Func<string, CancellationToken, Task<string?>> _asyncFetch;

    private PluginPayloadSource(
        Func<string, CancellationToken, Task<string?>> asyncFetch,
        Func<string, string?>? syncFetch = null)
    {
        _asyncFetch = asyncFetch;
        _syncFetch = syncFetch;
    }

    /// <summary>
    /// Creates a payload source backed by an asynchronous fetch callback.
    /// </summary>
    public static PluginPayloadSource FromAsync(Func<string, CancellationToken, Task<string?>> asyncFetch)
    {
        ArgumentNullException.ThrowIfNull(asyncFetch);
        return new PluginPayloadSource(asyncFetch);
    }

    /// <summary>
    /// Creates a payload source backed by a synchronous fetch callback.
    /// </summary>
    public static PluginPayloadSource FromSync(Func<string, string?> syncFetch)
    {
        ArgumentNullException.ThrowIfNull(syncFetch);
        return new PluginPayloadSource(
            (absoluteUrl, _) => Task.FromResult(syncFetch(absoluteUrl)),
            syncFetch);
    }

    /// <summary>
    /// Fetches payload content asynchronously for the supplied absolute URL.
    /// </summary>
    public Task<string?> FetchAsync(string? absoluteUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return Task.FromResult<string?>(null);
        }

        return _asyncFetch(absoluteUrl, cancellationToken);
    }

    /// <summary>
    /// Fetches payload content synchronously for the supplied absolute URL.
    /// </summary>
    public string? Fetch(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        return _syncFetch is not null
            ? _syncFetch(absoluteUrl)
            : _asyncFetch(absoluteUrl, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
    }
}