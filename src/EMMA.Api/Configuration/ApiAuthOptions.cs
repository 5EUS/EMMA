namespace EMMA.Api.Configuration;

public sealed record ApiKeyRecord(string Key, string ClientId);

public sealed class ApiAuthOptions
{
    public bool Enabled { get; init; } = true;
    public string HeaderName { get; init; } = "x-api-key";
    public List<ApiKeyRecord> Keys { get; init; } = [];
    public IReadOnlyList<string> AllowAnonymousPaths { get; init; } = ["/"];
}
