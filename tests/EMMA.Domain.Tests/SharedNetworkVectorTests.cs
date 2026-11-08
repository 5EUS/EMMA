using System.Text.Json;

using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>
/// The network allow-list is implemented twice, here (what the manifest grants) and in the Rust host (what is
/// enforced). Both read the same vector file, so they cannot drift apart.
/// </summary>
public sealed class SharedNetworkVectorTests
{
    /// <summary>Every case in the shared file gets the same answer from <see cref="CapabilityPolicy"/>.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CapabilityPolicyAgreesWithTheSharedVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vectors", "network-policy.json");
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement cases = document.RootElement.GetProperty("cases");
        int checkedCases = 0;

        for (int index = 0; index < cases.GetArrayLength(); index++)
        {
            JsonElement testCase = cases[index];
            JsonElement patternList = testCase.GetProperty("patterns");
            string[] patterns = [.. Enumerable.Range(0, patternList.GetArrayLength()).Select(i => patternList[i].GetString()!)];
            string url = testCase.GetProperty("url").GetString()!;
            bool allowed = testCase.GetProperty("allowed").GetBoolean();
            bool parsed = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri);

            bool actual = parsed && new CapabilityPolicy(patterns, cacheAllowed: false).AllowsNetwork(uri!);

            Assert.True(actual == allowed, $"{string.Join(",", patterns)} vs {url}: expected {allowed}");
            checkedCases++;
        }

        Assert.True(checkedCases >= 15);
    }
}
