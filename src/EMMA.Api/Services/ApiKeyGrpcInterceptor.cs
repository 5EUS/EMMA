using EMMA.Api.Configuration;
using EMMA.Domain;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace EMMA.Api.Services;

public sealed class ApiKeyGrpcInterceptor(
    IOptions<ApiAuthOptions> options,
    IApiKeyValidator validator,
    IClientIdentityAccessor identityAccessor,
    ILogger<ApiKeyGrpcInterceptor> logger) : Interceptor
{
    private readonly ApiAuthOptions _options = options.Value;
    private readonly IApiKeyValidator _validator = validator;
    private readonly IClientIdentityAccessor _identityAccessor = identityAccessor;
    private readonly ILogger<ApiKeyGrpcInterceptor> _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (!_options.Enabled)
        {
            return await continuation(request, context);
        }

        var httpContext = context.GetHttpContext();
        if (IsAnonymousPath(httpContext.Request.Path))
        {
            return await continuation(request, context);
        }

        var apiKey = context.RequestHeaders?.Get(_options.HeaderName)?.Value;
        if (!_validator.TryValidate(apiKey, out var identity))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, $"{ErrorCodes.Unauthenticated}: Invalid API key."));
        }

        _identityAccessor.Current = identity;
        httpContext.Items[ApiAuthHeader.ClientIdItemKey] = identity.ClientId;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ClientId"] = identity.ClientId
        });

        try
        {
            return await continuation(request, context);
        }
        finally
        {
            _identityAccessor.Current = null;
        }
    }

    private bool IsAnonymousPath(PathString path)
    {
        return _options.AllowAnonymousPaths.Any(allowed =>
            path.Equals(allowed, StringComparison.OrdinalIgnoreCase));
    }
}
