using EMMA.Domain;
using Grpc.Core;

namespace EMMA.PluginHost.Services;

internal sealed record PipelineError(string Code, string Message, string? Details = null);

internal sealed record PipelineErrorEnvelope(PipelineError Error);

internal static class PipelineErrorContract
{
    public static PipelineError FromException(Exception ex, string operation)
    {
        if (ex is RpcException rpcEx)
        {
            return FromRpcException(rpcEx, operation);
        }

        var code = ex switch
        {
            KeyNotFoundException => ErrorCodes.NotFound,
            TimeoutException => ErrorCodes.Timeout,
            OperationCanceledException => ErrorCodes.Cancelled,
            ArgumentException => ErrorCodes.InvalidRequest,
            InvalidDataException => ErrorCodes.InvalidRequest,
            InvalidOperationException when IsInvalidRequestMessage(ex.Message) => ErrorCodes.InvalidRequest,
            _ => ErrorCodes.UpstreamFailure
        };

        return new PipelineError(code, BuildMessage(code, operation, ex.Message), ex.Message);
    }

    public static IResult ToResult(Exception ex, string operation)
    {
        var error = FromException(ex, operation);
        return ToResult(error);
    }

    public static IResult ToResult(PipelineError error)
    {
        return Results.Json(
            new PipelineErrorEnvelope(error),
            PipelineErrorJsonContext.Default.PipelineErrorEnvelope,
            statusCode: ToHttpStatusCode(error.Code));
    }

    private static PipelineError FromRpcException(RpcException ex, string operation)
    {
        var code = ex.StatusCode switch
        {
            StatusCode.NotFound => ErrorCodes.NotFound,
            StatusCode.DeadlineExceeded => ErrorCodes.Timeout,
            StatusCode.Cancelled => ErrorCodes.Cancelled,
            StatusCode.InvalidArgument => ErrorCodes.InvalidRequest,
            StatusCode.Unauthenticated => ErrorCodes.Unauthenticated,
            _ => ErrorCodes.UpstreamFailure
        };

        var detail = string.IsNullOrWhiteSpace(ex.Status.Detail)
            ? ex.Message
            : ex.Status.Detail;

        return new PipelineError(code, BuildMessage(code, operation, detail), detail);
    }

    private static bool IsInvalidRequestMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("unsupported-operation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("must be", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMessage(string code, string operation, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail)
            && (code == ErrorCodes.InvalidRequest || code == ErrorCodes.NotFound))
        {
            return detail;
        }

        return code switch
        {
            ErrorCodes.InvalidRequest => $"{operation} request is invalid.",
            ErrorCodes.NotFound => $"{operation} resource was not found.",
            ErrorCodes.Timeout => $"{operation} timed out.",
            ErrorCodes.Cancelled => $"{operation} was cancelled.",
            ErrorCodes.Unauthenticated => $"{operation} is not authenticated.",
            _ => $"{operation} failed."
        };
    }

    private static int ToHttpStatusCode(string code)
    {
        return code switch
        {
            ErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
            ErrorCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            ErrorCodes.Cancelled => 499,
            _ => StatusCodes.Status502BadGateway
        };
    }
}