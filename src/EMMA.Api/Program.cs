using EMMA.Api;
using EMMA.Api.Security;
using EMMA.Domain;

if (args is ["--new-api-key", string clientId])
{
    if (!UserId.TryCreate(clientId, out _))
    {
        await Console.Error.WriteLineAsync("The client id must be non-blank and at most 128 characters.").ConfigureAwait(false);
        return 2;
    }

    GeneratedApiKey generated = ApiKeys.Generate();
    await Console.Out.WriteLineAsync(
        $"API key for '{clientId}' (shown once; give it to the client in the x-api-key header):\n  {generated.Key}\n\n"
        + $"Add this to Emma:Api:Keys in the host configuration:\n  {{ \"ClientId\": \"{clientId}\", \"KeyHash\": \"{generated.Hash}\" }}").ConfigureAwait(false);
    return 0;
}

WebApplication app = await ApiHost.BuildAsync(args).ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);
return 0;
