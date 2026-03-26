using EMMA.Api.Configuration;
using Microsoft.Extensions.Options;

namespace EMMA.Api.Services;

public interface IApiKeyValidator
{
    bool TryValidate(string? apiKey, out ApiClientIdentity identity);
}

public sealed record ApiClientIdentity(string ClientId, string ApiKey);

public sealed class ApiKeyValidator : IApiKeyValidator
{
    private readonly Dictionary<string, ApiClientIdentity> _keys;

    public ApiKeyValidator(IOptions<ApiAuthOptions> options)
    {
        _keys = options.Value.Keys
            .Where(record => !string.IsNullOrWhiteSpace(record.Key))
            .DistinctBy(record => record.Key, StringComparer.Ordinal)
            .ToDictionary(
                record => record.Key,
                record => new ApiClientIdentity(record.ClientId, record.Key),
                StringComparer.Ordinal);
    }

    public bool TryValidate(string? apiKey, out ApiClientIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            identity = default!;
            return false;
        }

        return _keys.TryGetValue(apiKey, out identity!);
    }
}
