using EMMA.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EMMA.Api.Services;

public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiAuthOptions _options;
    private readonly IApiKeyValidator _validator;
    private readonly IClientIdentityAccessor _identityAccessor;

    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        IOptions<ApiAuthOptions> options,
        IApiKeyValidator validator,
        IClientIdentityAccessor identityAccessor)
    {
        _next = next;
        _options = options.Value;
        _validator = validator;
        _identityAccessor = identityAccessor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || IsAnonymousPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (ApiAuthHeader.IsGrpcRequest(context))
        {
            await _next(context);
            return;
        }

        var apiKey = context.Request.Headers[_options.HeaderName].ToString();
        if (!_validator.TryValidate(apiKey, out var identity))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        _identityAccessor.Current = identity;
        context.Items[ApiAuthHeader.ClientIdItemKey] = identity.ClientId;

        try
        {
            await _next(context);
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
