using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.PluginHost.Signing;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="PluginSignatureVerifier"/>, written as the attacks it must stop.</summary>
public sealed class PluginSignatureVerifierTests
{
    private readonly FakeTimeProvider time = new(TestPki.Now);
    private readonly FakeDelegationSource source = new();
    private readonly FakeDelegationVersions versions = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A properly signed plugin from a trusted repository is accepted, and the signer is recorded.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AValidChainIsAccepted()
    {
        this.Publish();

        Outcome<VerifiedSignature> result = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("test-repo/signer-1", result.Value.Signer);
        Assert.Equal(1L, this.versions.Highest(TestPki.RepositoryId));
    }

    /// <summary>An unsigned manifest is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnsignedManifestIsRejected()
    {
        this.Publish();

        Outcome<VerifiedSignature> result = await this.VerifyAsync(TestManifests.Valid());

        Assert.Equal(ErrorCode.Unauthorized, result.Error?.Code);
        Assert.Contains("not signed", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Changing any signed field after signing is detected, including swapping the code (entrypoint digest) or widening permissions.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnyChangeAfterSigningIsDetected()
    {
        this.Publish();
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());
        PluginManifest[] tampered =
        [
            signed with { Name = "Evil" },
            signed with { Version = "9.9.9" },
            signed with { Entrypoint = signed.Entrypoint with { Sha256 = new string('f', 64) } },
            signed with { Permissions = signed.Permissions with { Network = [.. signed.Permissions.Network, "evil.example.net"] } },
            signed with { Limits = signed.Limits with { MemoryMb = 4096 } },
            signed with { Contract = signed.Contract with { Required = ["emma:core/meta@1"] } },
            signed with { Kinds = [NodeKind.Create("other")] },
        ];

        foreach (PluginManifest manifest in tampered)
        {
            Outcome<VerifiedSignature> result = await this.VerifyAsync(manifest);
            Assert.Contains("changed after it was signed", result.Error?.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A signature with one bit flipped does not verify.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ATamperedSignatureIsRejected()
    {
        this.Publish();
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());
        byte[] bytes = Convert.FromBase64String(signed.Signature!.Value);
        bytes[10] ^= 0x01;

        Outcome<VerifiedSignature> result = await this.VerifyAsync(signed with { Signature = signed.Signature with { Value = Convert.ToBase64String(bytes) } });

        Assert.Contains("does not verify", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>An attacker's own key, claiming a delegated key's id, cannot pass; nor can a key id nobody delegated.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ForgedAndUnknownKeysAreRejected()
    {
        this.Publish();

        Outcome<VerifiedSignature> impostor = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid(), key: TestPki.Attacker));
        Outcome<VerifiedSignature> unknown = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid(), key: TestPki.Attacker, keyId: "attacker-1"));

        Assert.Contains("does not verify", impostor.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("not delegated", unknown.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A key is limited to its scopes, matched by whole segments: <c>com.acme.*</c> does not cover <c>com.acmevil.x</c>.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ScopesAreMatchedBySegment()
    {
        (string Scope, string Plugin, bool Allowed)[] cases =
        [
            ("com.acme.*", "com.acme.manga", true),
            ("com.acme.*", "com.acme.deep.nested", true),
            ("com.acme.*", "com.acmevil.manga", false),
            ("com.acme.*", "com.acme", false),
            ("com.acme.manga", "com.acme.manga", true),
            ("com.acme.manga", "com.acme.manga2", false),
            ("com.*", "com.anything.at.all", true),
            ("org.*", "com.example.test", false),
        ];

        foreach ((string scope, string plugin, bool allowed) in cases)
        {
            this.Publish(TestPki.Bundle(1, TestPki.Delegate([scope])));

            Outcome<VerifiedSignature> result = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid(plugin)));

            Assert.True(result.IsSuccess == allowed, $"{scope} vs {plugin}: {result.Error?.Message}");
            if (!allowed)
            {
                Assert.Contains("not authorized", result.Error?.Message, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A revoked key is refused, a retired key is honoured for signatures made while it was valid, and a key outside its window is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task KeyStatusAndWindowAreEnforced()
    {
        this.Publish(TestPki.Bundle(1, TestPki.Delegate(status: DelegationStatus.Revoked)));
        Assert.Contains("revoked", (await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()))).Error?.Message, StringComparison.Ordinal);

        this.Publish(TestPki.Bundle(2, TestPki.Delegate(status: DelegationStatus.Retired)));
        Assert.True((await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()))).IsSuccess);

        this.Publish(TestPki.Bundle(3, TestPki.Delegate()));
        PluginManifest tooEarly = TestPki.Sign(TestManifests.Valid(), issuedAt: TestPki.Now.AddDays(-40));
        PluginManifest tooLate = TestPki.Sign(TestManifests.Valid(), issuedAt: TestPki.Now.AddDays(-1), expiresAt: TestPki.Now.AddDays(90));
        Assert.Contains("not valid when", (await this.VerifyAsync(tooEarly)).Error?.Message, StringComparison.Ordinal);
        this.time.Advance(TimeSpan.FromDays(35));
        Assert.True((await this.VerifyAsync(tooLate)).IsSuccess);
    }

    /// <summary>An expired signature is refused, a future-dated one is refused beyond a small skew, and slightly early clocks are tolerated.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SignatureTimesAreEnforced()
    {
        this.Publish();
        PluginManifest expiring = TestPki.Sign(TestManifests.Valid(), expiresAt: TestPki.Now.AddMinutes(1));
        PluginManifest future = TestPki.Sign(TestManifests.Valid(), issuedAt: TestPki.Now.AddHours(2), expiresAt: TestPki.Now.AddDays(1));
        PluginManifest slightlyEarly = TestPki.Sign(TestManifests.Valid(), issuedAt: TestPki.Now.AddMinutes(4), expiresAt: TestPki.Now.AddDays(1));

        Assert.Contains("future", (await this.VerifyAsync(future)).Error?.Message, StringComparison.Ordinal);
        Assert.True((await this.VerifyAsync(slightlyEarly)).IsSuccess);
        Assert.True((await this.VerifyAsync(expiring)).IsSuccess);

        this.time.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains("expired", (await this.VerifyAsync(expiring)).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>The repository id comes from the package source: a signature naming another repository, or an untrusted one, is refused before any file is read.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RepositoryComesFromTheSourceAndMustBeTrusted()
    {
        this.Publish();
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());
        PluginManifest rogue = PluginSigner.SignManifest(TestManifests.Valid(), "rogue", TestPki.SignerKeyId, TestPki.Signer.PrivatePem, TestPki.Now.AddHours(-1), TestPki.Now.AddDays(1));

        Outcome<VerifiedSignature> mismatch = await this.VerifyAsync(signed, "other-repo");
        Outcome<VerifiedSignature> untrusted = await this.VerifyAsync(rogue, "rogue");

        Assert.Contains("different one", mismatch.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("not trusted", untrusted.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(0, this.source.Calls);
    }

    /// <summary>A repository id that is not a plain identifier, such as a path, never reaches the delegation source.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HostileRepositoryIdsNeverReachTheSource()
    {
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());

        foreach (string hostile in new[] { "../etc/passwd", "a/b", "a\\b", string.Empty, "a b", "..", "x/../../y" })
        {
            Outcome<VerifiedSignature> result = await this.VerifyAsync(signed, hostile);

            Assert.False(result.IsSuccess);
        }

        Assert.Equal(0, this.source.Calls);
    }

    /// <summary>A missing, malformed, mislabelled or forged delegation file is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BadDelegationFilesAreRejected()
    {
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());
        Assert.Contains("no delegation file", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);

        this.source.With(TestPki.RepositoryId, "not json"u8.ToArray());
        Assert.Contains("malformed", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);

        DelegationBundle otherRepo = PluginSigner.SignDelegations("other-repo", 1, TestPki.Now, TestPki.RootKeyId, [TestPki.Delegate()], TestPki.Root.PrivatePem);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(otherRepo));
        Assert.Contains("different repository", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);

        DelegationBundle unknownRoot = PluginSigner.SignDelegations(TestPki.RepositoryId, 1, TestPki.Now, "root-9", [TestPki.Delegate()], TestPki.Root.PrivatePem);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(unknownRoot));
        Assert.Contains("root key 'root-9' is not trusted", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);

        DelegationBundle forged = PluginSigner.SignDelegations(TestPki.RepositoryId, 1, TestPki.Now, TestPki.RootKeyId, [TestPki.Delegate()], TestPki.Attacker.PrivatePem);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(forged));
        Assert.Contains("root signature", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Editing a validly signed delegation file (adding a signer, widening a scope, un-revoking a key) breaks its root signature.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EditingASignedDelegationFileIsDetected()
    {
        PluginManifest attackerSigned = TestPki.Sign(TestManifests.Valid(), key: TestPki.Attacker, keyId: "attacker-1");
        DelegationBundle genuine = TestPki.Bundle(1, TestPki.Delegate(["com.example.manga"]));

        DelegationBundle withExtraSigner = genuine with { Delegations = [.. genuine.Delegations, TestPki.Delegate(["com.*"], key: TestPki.Attacker, keyId: "attacker-1")] };
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(withExtraSigner));
        Assert.Contains("root signature", (await this.VerifyAsync(attackerSigned)).Error?.Message, StringComparison.Ordinal);

        DelegationBundle widened = genuine with { Delegations = [genuine.Delegations[0] with { Scopes = ["com.*"] }] };
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(widened));
        Assert.Contains("root signature", (await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()))).Error?.Message, StringComparison.Ordinal);

        DelegationBundle revoked = TestPki.Bundle(2, TestPki.Delegate(status: DelegationStatus.Revoked));
        DelegationBundle unrevoked = revoked with { Delegations = [revoked.Delegations[0] with { Status = DelegationStatus.Active }] };
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(unrevoked));
        Assert.Contains("root signature", (await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()))).Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A delegated key that is too weak is refused when the file is read.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task WeakDelegatedKeysAreRejected()
    {
        string json = System.Text.Encoding.UTF8.GetString(DelegationJson.Write(TestPki.Bundle()));
        string weak = System.Text.Json.JsonEncodedText.Encode(TestPki.Weak.PublicPem).ToString();
        string original = System.Text.Json.JsonEncodedText.Encode(TestPki.Signer.PublicPem).ToString();
        this.source.With(TestPki.RepositoryId, System.Text.Encoding.UTF8.GetBytes(json.Replace(original, weak, StringComparison.Ordinal)));

        Outcome<VerifiedSignature> result = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        Assert.Contains("malformed", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A delegation file dated in the future is refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AFutureDatedDelegationFileIsRejected()
    {
        DelegationBundle future = PluginSigner.SignDelegations(TestPki.RepositoryId, 1, TestPki.Now.AddHours(2), TestPki.RootKeyId, [TestPki.Delegate()], TestPki.Root.PrivatePem);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(future));

        Outcome<VerifiedSignature> result = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        Assert.Contains("future", result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>An old, validly signed delegation file (which still lists a since-revoked key as active) cannot be replayed once a newer one was accepted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnOldDelegationFileCannotBeReplayed()
    {
        PluginManifest signed = TestPki.Sign(TestManifests.Valid());
        DelegationBundle old = TestPki.Bundle(1, TestPki.Delegate());
        DelegationBundle revokedLater = TestPki.Bundle(2, TestPki.Delegate(status: DelegationStatus.Revoked));

        this.Publish(old);
        Assert.True((await this.VerifyAsync(signed)).IsSuccess);

        this.Publish(revokedLater);
        Assert.Contains("revoked", (await this.VerifyAsync(signed)).Error?.Message, StringComparison.Ordinal);

        this.Publish(old);
        Outcome<VerifiedSignature> replay = await this.VerifyAsync(signed);

        Assert.Contains("older than one already accepted", replay.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(2L, this.versions.Highest(TestPki.RepositoryId));
    }

    /// <summary>A forged delegation file with a huge version number cannot lock real updates out, because only an authentic file moves the counter.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AForgedFileDoesNotAdvanceTheVersionCounter()
    {
        DelegationBundle forged = PluginSigner.SignDelegations(TestPki.RepositoryId, 99, TestPki.Now, TestPki.RootKeyId, [TestPki.Delegate()], TestPki.Attacker.PrivatePem);
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(forged));
        await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        this.Publish(TestPki.Bundle(2, TestPki.Delegate()));
        Outcome<VerifiedSignature> genuine = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        Assert.True(genuine.IsSuccess, genuine.Error?.Message);
        Assert.Equal(2L, this.versions.Highest(TestPki.RepositoryId));
    }

    /// <summary>With several delegated signers the signature's key id selects among them.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheKeyIdSelectsAmongSeveralSigners()
    {
        DelegatedSigner second = TestPki.Delegate(["com.example.*"], key: TestPki.Attacker, keyId: "signer-2");
        this.Publish(TestPki.Bundle(1, TestPki.Delegate(["org.nothing.*"]), second));

        Outcome<VerifiedSignature> viaSecond = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid(), key: TestPki.Attacker, keyId: "signer-2"));
        Outcome<VerifiedSignature> viaFirst = await this.VerifyAsync(TestPki.Sign(TestManifests.Valid()));

        Assert.True(viaSecond.IsSuccess, viaSecond.Error?.Message);
        Assert.Contains("not authorized", viaFirst.Error?.Message, StringComparison.Ordinal);
    }

    private void Publish(DelegationBundle? bundle = null) =>
        this.source.With(TestPki.RepositoryId, DelegationJson.Write(bundle ?? TestPki.Bundle()));

    private Task<Outcome<VerifiedSignature>> VerifyAsync(PluginManifest manifest, string repositoryId = TestPki.RepositoryId) =>
        new PluginSignatureVerifier(TestPki.Policy(), this.source, this.versions, this.time).VerifyAsync(manifest, repositoryId, Ct);
}
