namespace EMMA.Api.Tests;

/// <summary>Shared helpers for tests that need a running server.</summary>
internal static class ApiTests
{
    /// <summary>Runs a test against a fresh server and disposes it afterwards.</summary>
    /// <param name="test">The test body.</param>
    /// <param name="perClientPermits">The per-client request allowance.</param>
    /// <returns>A task that completes when the test has run.</returns>
    public static async Task WithServerAsync(Func<ApiTestServer, Task> test, int perClientPermits = 1000)
    {
        ApiTestServer server = await ApiTestServer.StartAsync(perClientPermits).ConfigureAwait(true);
        await using (server.ConfigureAwait(true))
        {
            await test(server).ConfigureAwait(true);
        }
    }
}
