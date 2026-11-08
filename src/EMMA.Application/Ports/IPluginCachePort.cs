using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>The private, expiring key/value cache behind <c>host.cache-get</c> and <c>host.cache-put</c>. One namespace per plugin.</summary>
public interface IPluginCachePort
{
    /// <summary>Reads an entry.</summary>
    /// <param name="plugin">The owning plugin.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or <see langword="null"/> when missing or expired.</returns>
    Task<ReadOnlyMemory<byte>?> GetAsync(PluginId plugin, string key, CancellationToken cancellationToken);

    /// <summary>Writes an entry.</summary>
    /// <param name="plugin">The owning plugin.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The bytes.</param>
    /// <param name="timeToLive">How long the entry may live.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    Task SetAsync(
        PluginId plugin,
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);
}
