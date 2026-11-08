using EMMA.Domain;

namespace EMMA.Api.Security;

/// <summary>The authenticated caller. Everything a request does on its own behalf is filed under this identity.</summary>
/// <param name="ClientId">The client id configured for the key.</param>
internal sealed record ApiClient(string ClientId)
{
    /// <summary>Gets the user whose progress and library this client reads and writes.</summary>
    public UserId User => UserId.Create(this.ClientId);
}
