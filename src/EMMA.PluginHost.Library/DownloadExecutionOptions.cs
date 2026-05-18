using System.Threading;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Configures how many download jobs may execute concurrently.
/// </summary>
public sealed class DownloadExecutionOptions
{
    /// <summary>
    /// The default maximum number of concurrent downloads.
    /// </summary>
    public const int DefaultMaxConcurrentDownloads = 1;

    /// <summary>
    /// The minimum allowed maximum number of concurrent downloads.
    /// </summary>
    public const int MinMaxConcurrentDownloads = 1;

    /// <summary>
    /// The maximum allowed maximum number of concurrent downloads.
    /// </summary>
    public const int MaxMaxConcurrentDownloads = 8;

    private int _maxConcurrentDownloads = DefaultMaxConcurrentDownloads;

    /// <summary>
    /// Gets or sets the maximum number of download jobs that may run at the same time.
    /// </summary>
    public int MaxConcurrentDownloads
    {
        get => Volatile.Read(ref _maxConcurrentDownloads);
        set => Volatile.Write(ref _maxConcurrentDownloads, ClampMaxConcurrentDownloads(value));
    }

    /// <summary>
    /// Clamps a requested concurrency limit into the supported range.
    /// </summary>
    /// <param name="value">The requested maximum concurrent download count.</param>
    /// <returns>The clamped maximum concurrent download count.</returns>
    public static int ClampMaxConcurrentDownloads(int value)
    {
        if (value < MinMaxConcurrentDownloads)
        {
            return MinMaxConcurrentDownloads;
        }

        if (value > MaxMaxConcurrentDownloads)
        {
            return MaxMaxConcurrentDownloads;
        }

        return value;
    }
}