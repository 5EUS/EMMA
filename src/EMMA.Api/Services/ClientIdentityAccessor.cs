namespace EMMA.Api.Services;

public interface IClientIdentityAccessor
{
    ApiClientIdentity? Current { get; set; }
}

public sealed class ClientIdentityAccessor : IClientIdentityAccessor
{
    private static readonly AsyncLocal<ApiClientIdentity?> CurrentIdentity = new();

    public ApiClientIdentity? Current
    {
        get => CurrentIdentity.Value;
        set => CurrentIdentity.Value = value;
    }
}
