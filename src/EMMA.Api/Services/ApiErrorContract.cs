using EMMA.Contracts.Api.V1;
using EMMA.Domain;

namespace EMMA.Api.Services;

public static class ApiErrorContract
{
    public static ApiError FromException(Exception ex)
    {
        var code = ex switch
        {
            KeyNotFoundException => ErrorCodes.NotFound,
            TimeoutException => ErrorCodes.Timeout,
            OperationCanceledException => ErrorCodes.Cancelled,
            ArgumentException => ErrorCodes.InvalidRequest,
            InvalidOperationException => ErrorCodes.InvalidRequest,
            _ => ErrorCodes.UpstreamFailure
        };

        return new ApiError
        {
            Code = code,
            Message = string.IsNullOrWhiteSpace(ex.Message) ? "Request failed." : ex.Message
        };
    }

    public static ApiError InvalidRequest(string message)
    {
        return new ApiError
        {
            Code = ErrorCodes.InvalidRequest,
            Message = message
        };
    }

    public static int ToHttpStatusCode(ApiError error)
    {
        return error.Code switch
        {
            ErrorCodes.InvalidRequest => 400,
            ErrorCodes.Unauthenticated => 401,
            ErrorCodes.NotFound => 404,
            ErrorCodes.Timeout => 504,
            ErrorCodes.Cancelled => 499,
            _ => 502
        };
    }

    public static object ToEnvelope(ApiError error)
    {
        return new
        {
            error = new
            {
                code = error.Code,
                message = error.Message,
                details = error.Details
            }
        };
    }
}