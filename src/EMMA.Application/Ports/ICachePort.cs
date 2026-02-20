namespace EMMA.Application.Ports;

/// <summary>
/// Cache abstraction for pipeline-level results with explicit TTL semantics.
/// </summary>
public interface ICachePort
{
    /// <summary>
    /// Retrieves a cached value or null if missing/expired.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class;

    /// <summary>
    /// Stores a value with a time-to-live.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="ttl"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) where T : class;
}
