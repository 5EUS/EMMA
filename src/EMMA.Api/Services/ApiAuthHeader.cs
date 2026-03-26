using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace EMMA.Api.Services;

public static class ApiAuthHeader
{
    public const string ClientIdItemKey = "client_id";

    public static string GetClientKey(HttpContext context, string headerName)
    {
        if (context.Items.TryGetValue(ClientIdItemKey, out var value)
            && value is string clientId
            && !string.IsNullOrWhiteSpace(clientId))
        {
            return clientId;
        }

        if (context.Request.Headers.TryGetValue(headerName, out StringValues header)
            && !StringValues.IsNullOrEmpty(header))
        {
            return header.ToString();
        }

        return "anonymous";
    }

    public static bool IsGrpcRequest(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }
}
