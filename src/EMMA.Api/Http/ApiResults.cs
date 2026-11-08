using System.Text.Json.Serialization.Metadata;

using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Http;

/// <summary>Builds every response the API sends, so success and failure always have the same shape.</summary>
internal static class ApiResults
{
    /// <summary>Builds a 200 response.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="value">The body.</param>
    /// <param name="typeInfo">How to write the body.</param>
    /// <returns>The result.</returns>
    public static IResult Ok<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new JsonResult<T>(StatusCodes.Status200OK, value, typeInfo);

    /// <summary>Builds the response for a failed plugin or pipeline call.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>The result, with the status and code the failure class maps to.</returns>
    public static IResult Failure(PluginError error)
    {
        (int status, string code) = Describe(error.Code);
        return Error(status, code, error.Message, error.RetryAfter);
    }

    /// <summary>Builds a failure response for a request the API itself refused.</summary>
    /// <param name="code">The failure class.</param>
    /// <param name="message">A message that is safe to show.</param>
    /// <returns>The result.</returns>
    public static IResult Failure(ErrorCode code, string message) => Failure(new PluginError(code, message));

    /// <summary>Builds an error response.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The stable machine-readable code.</param>
    /// <param name="message">A message that is safe to show.</param>
    /// <param name="retryAfter">When to retry, if known.</param>
    /// <returns>The result.</returns>
    public static IResult Error(int status, string code, string message, TimeSpan? retryAfter = null)
    {
        ErrorResponse body = new(new ErrorBody(code, message, retryAfter is { } delay ? (long)delay.TotalMilliseconds : null));
        return new JsonResult<ErrorResponse>(status, body, ApiJsonContext.Default.ErrorResponse, retryAfter);
    }

    private static (int Status, string Code) Describe(ErrorCode code) => code switch
    {
        ErrorCode.NotFound => (StatusCodes.Status404NotFound, "not_found"),
        ErrorCode.InvalidArgument => (StatusCodes.Status400BadRequest, "invalid_argument"),
        ErrorCode.Unsupported => (StatusCodes.Status501NotImplemented, "unsupported"),
        ErrorCode.RateLimited => (StatusCodes.Status429TooManyRequests, "upstream_rate_limited"),
        ErrorCode.Unavailable => (StatusCodes.Status503ServiceUnavailable, "unavailable"),
        ErrorCode.Cancelled => (StatusCodes.Status504GatewayTimeout, "timeout"),

        // The plugin's own upstream wants credentials from the user. That is not this API's 401.
        ErrorCode.Unauthorized => (StatusCodes.Status424FailedDependency, "plugin_unauthorized"),
        _ => (StatusCodes.Status502BadGateway, "plugin_error"),
    };
}
