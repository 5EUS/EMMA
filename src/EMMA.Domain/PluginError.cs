namespace EMMA.Domain;

/// <summary>The error type of a failed plugin call.</summary>
/// <param name="Code">The failure class.</param>
/// <param name="Message">A human-readable message that is safe to show.</param>
/// <param name="RetryAfter">When the upstream said when to retry, if it did.</param>
public sealed record PluginError(ErrorCode Code, string Message, TimeSpan? RetryAfter = null)
{
    /// <summary>Gets a value indicating whether retrying the same call later can succeed.</summary>
    public bool IsTransient => this.Code is ErrorCode.RateLimited or ErrorCode.Unavailable;
}
