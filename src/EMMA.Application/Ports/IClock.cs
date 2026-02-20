namespace EMMA.Application.Ports;

/// <summary>
/// Abstraction for current time to keep pipelines testable.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
