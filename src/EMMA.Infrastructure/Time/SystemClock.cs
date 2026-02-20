using EMMA.Application.Ports;

namespace EMMA.Infrastructure.Time;

/// <summary>
/// System time adapter for UTC timestamps.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
