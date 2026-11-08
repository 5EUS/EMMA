using System.Text;
using System.Text.Json;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for delegation files: parsing, scopes, the file source and the full sign, serialize, parse, verify round trip.</summary>
public sealed class DelegationFileTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-delegation-test-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BundleJson => Encoding.UTF8.GetString(DelegationJson.Write(TestPki.Bundle(3, TestPki.Delegate())));

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose() => this.directory.Delete(recursive: true);

    /// <summary>A signed bundle and a signed manifest survive serialization, and the parsed copies still verify.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SignSerializeParseVerifyRoundTrips()
    {
        byte[] bundleBytes = DelegationJson.Write(TestPki.Bundle(7, TestPki.Delegate()));
        byte[] manifestBytes = ManifestWriter.Write(TestPki.Sign(TestManifests.Valid()));
        DelegationBundle parsedBundle = DelegationJson.Parse(bundleBytes).Value;
        PluginManifest parsedManifest = ManifestParser.Parse(manifestBytes, requireSignature: true).Value;
        FakeTimeProvider time = new(TestPki.Now);

        Outcome<VerifiedSignature> result = await new PluginSignatureVerifier(
            TestPki.Policy(),
            new FakeDelegationSource().With(TestPki.RepositoryId, DelegationJson.Write(parsedBundle)),
            new FakeDelegationVersions(),
            time).VerifyAsync(parsedManifest, TestPki.RepositoryId, Ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(7L, parsedBundle.Version);
        Assert.Equal(bundleBytes, DelegationJson.Write(parsedBundle));
    }

    /// <summary>Each malformed delegation file is rejected, naming the field.</summary>
    /// <param name="find">Text in the valid file to replace.</param>
    /// <param name="replace">The replacement.</param>
    /// <param name="field">The field the error must mention.</param>
    [Theory]
    [InlineData("\"repositoryId\": \"test-repo\"", "\"repositoryId\": \"../x\"", "$.repositoryId")]
    [InlineData("\"version\": 3", "\"version\": -1", "$.version")]
    [InlineData("\"version\": 3", "\"version\": 3.5", "$.version")]
    [InlineData("\"scopes\": [\n        \"com.example.*\"\n      ]", "\"scopes\": []", "scopes")]
    [InlineData("\"com.example.*\"", "\"*\"", "scopes")]
    [InlineData("\"com.example.*\"", "\"Com.Example\"", "scopes")]
    [InlineData("\"com.example.*\"", "\"com.*.x\"", "scopes")]
    [InlineData("\"status\": \"ACTIVE\"", "\"status\": \"MAYBE\"", "status")]
    [InlineData("\"algorithm\": \"rsa-sha256\"", "\"algorithm\": \"md5\"", "algorithm")]
    public void MalformedFilesAreRejected(string find, string replace, string field)
    {
        string json = BundleJson;
        Assert.Contains(find, json, StringComparison.Ordinal);

        Outcome<DelegationBundle> parsed = DelegationJson.Parse(Encoding.UTF8.GetBytes(json.Replace(find, replace, StringComparison.Ordinal)));

        Assert.False(parsed.IsSuccess);
        Assert.Contains(field, parsed.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown and repeated properties, and an inverted validity window, are rejected.</summary>
    [Fact]
    public void UnknownRepeatedAndInvertedAreRejected()
    {
        string json = BundleJson;
        string unknown = json.Replace("\"version\": 3,", "\"version\": 3, \"admin\": true,", StringComparison.Ordinal);
        string repeated = json.Replace("\"version\": 3,", "\"version\": 3, \"version\": 4,", StringComparison.Ordinal);
        string inverted = json.Replace("validFrom", "tmp", StringComparison.Ordinal).Replace("validUntil", "validFrom", StringComparison.Ordinal).Replace("tmp", "validUntil", StringComparison.Ordinal);

        Assert.Contains("$.admin", DelegationJson.Parse(Encoding.UTF8.GetBytes(unknown)).Error?.Message, StringComparison.Ordinal);
        Assert.False(DelegationJson.Parse(Encoding.UTF8.GetBytes(repeated)).IsSuccess);
        Assert.Contains("validUntil", DelegationJson.Parse(Encoding.UTF8.GetBytes(inverted)).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A delegated key must be a public RSA key: a private key, a certificate label or plain text is refused.</summary>
    [Fact]
    public void OnlyPublicKeysAreAccepted()
    {
        string json = BundleJson;
        string publicPem = JsonEncodedText.Encode(TestPki.Signer.PublicPem).ToString();
        string[] bad =
        [
            JsonEncodedText.Encode(TestPki.Signer.PrivatePem).ToString(),
            JsonEncodedText.Encode(TestPki.Signer.PublicPem.Replace("PUBLIC KEY", "CERTIFICATE", StringComparison.Ordinal)).ToString(),
            "not a key",
        ];

        foreach (string replacement in bad)
        {
            Outcome<DelegationBundle> parsed = DelegationJson.Parse(Encoding.UTF8.GetBytes(json.Replace(publicPem, replacement, StringComparison.Ordinal)));

            Assert.Contains("publicKeyPem", parsed.Error?.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Two delegations with the same key id are rejected.</summary>
    [Fact]
    public void DuplicateKeyIdsAreRejected()
    {
        DelegationBundle bundle = TestPki.Bundle(1, TestPki.Delegate(), TestPki.Delegate());

        Assert.Contains("twice", DelegationJson.Parse(DelegationJson.Write(bundle)).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Scopes are valid only as exact plugin ids or <c>prefix.*</c>, and match whole segments.</summary>
    [Fact]
    public void ScopesValidateAndMatch()
    {
        Assert.True(DelegationScope.IsValid("com.example.manga"));
        Assert.True(DelegationScope.IsValid("com.example.*"));
        Assert.True(DelegationScope.IsValid("com.*"));
        Assert.False(DelegationScope.IsValid("*"));
        Assert.False(DelegationScope.IsValid(".*"));
        Assert.False(DelegationScope.IsValid("com.*.manga"));
        Assert.False(DelegationScope.IsValid("com.example*"));
        Assert.False(DelegationScope.IsValid(string.Empty));

        PluginId manga = PluginId.Create("com.example.manga");
        Assert.True(DelegationScope.Covers("com.example.*", manga));
        Assert.False(DelegationScope.Covers("com.exam.*", manga));
        Assert.False(DelegationScope.Covers("com.example.manga.*", manga));
    }

    /// <summary>The file source reads a repository's file, and returns nothing for a missing or oversized one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheFileSourceReadsOnlyItsOwnFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(this.directory.FullName, "good.delegations.json"), [1, 2, 3], Ct);
        await File.WriteAllBytesAsync(Path.Combine(this.directory.FullName, "big.delegations.json"), new byte[DelegationJson.MaxBytes + 1], Ct);
        FileDelegationSource source = new(this.directory.FullName);

        Assert.Equal([1, 2, 3], (await source.GetAsync("good", Ct))!.Value.ToArray());
        Assert.Null(await source.GetAsync("missing", Ct));
        Assert.Null(await source.GetAsync("big", Ct));
    }

    /// <summary>A repository id that is a path never reads outside the directory, even when such a file exists.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheFileSourceRefusesPathsAsRepositoryIds()
    {
        DirectoryInfo inner = this.directory.CreateSubdirectory("inner");
        await File.WriteAllBytesAsync(Path.Combine(this.directory.FullName, "secret.delegations.json"), [9], Ct);
        FileDelegationSource source = new(inner.FullName);

        foreach (string hostile in new[] { "../secret", "..", "a/b", "a\\b", string.Empty, "inner/../secret" })
        {
            Assert.Null(await source.GetAsync(hostile, Ct));
        }
    }
}
