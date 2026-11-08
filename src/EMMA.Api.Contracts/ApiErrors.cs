using EMMA.Domain;

namespace EMMA.Api.Contracts;

/// <summary>
/// How a failure is described to clients: a stable machine-readable code and the HTTP status that goes with it.
/// The HTTP API sends the status; the native library has no HTTP and sends only the code, so a client handles one
/// vocabulary on both.
/// </summary>
public static class ApiErrors
{
    /// <summary>Describes a plugin or pipeline failure class.</summary>
    /// <param name="code">The failure class.</param>
    /// <returns>The HTTP status and the stable error code.</returns>
    public static (int Status, string Code) Describe(ErrorCode code) => code switch
    {
        ErrorCode.NotFound => (404, "not_found"),
        ErrorCode.InvalidArgument => (400, "invalid_argument"),
        ErrorCode.Unsupported => (501, "unsupported"),
        ErrorCode.RateLimited => (429, "upstream_rate_limited"),
        ErrorCode.Unavailable => (503, "unavailable"),
        ErrorCode.Cancelled => (504, "timeout"),

        // The plugin's own upstream wants credentials from the user. That is not this API's 401.
        ErrorCode.Unauthorized => (424, "plugin_unauthorized"),
        _ => (502, "plugin_error"),
    };

    /// <summary>Builds the error body for a failure.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>The body.</returns>
    public static ErrorResponse ToResponse(PluginError error) =>
        ToResponse(Describe(error.Code).Code, error.Message, error.RetryAfter);

    /// <summary>Builds an error body.</summary>
    /// <param name="code">The stable machine-readable code.</param>
    /// <param name="message">A message that is safe to show.</param>
    /// <param name="retryAfter">When to retry, if known.</param>
    /// <returns>The body.</returns>
    public static ErrorResponse ToResponse(string code, string message, TimeSpan? retryAfter = null) =>
        new(new ErrorBody(code, message, retryAfter is { } delay ? (long)delay.TotalMilliseconds : null));
}
