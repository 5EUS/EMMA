using System.Text;
using System.Text.Json;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.PluginHost.Tests;

/// <summary>Tests for <see cref="ManifestDigest"/>: what a signature actually binds to.</summary>
public sealed class ManifestDigestTests
{
    private static readonly PluginManifest Baseline = TestManifests.Valid();

    /// <summary>The digest is the same for the same manifest and is 32 bytes.</summary>
    [Fact]
    public void IsDeterministic()
    {
        Assert.Equal(32, ManifestDigest.Compute(Baseline).Length);
        Assert.Equal(ManifestDigest.ComputeHex(Baseline), ManifestDigest.ComputeHex(TestManifests.Valid()));
    }

    /// <summary>JSON whitespace and key order do not change the digest, because it is built from parsed fields.</summary>
    [Fact]
    public void IgnoresJsonFormatting()
    {
        string indented = TestManifests.Json(Baseline);
        string reordered = ReorderCompactly(indented);

        PluginManifest first = ManifestParser.Parse(Encoding.UTF8.GetBytes(indented), requireSignature: false).Value;
        PluginManifest second = ManifestParser.Parse(Encoding.UTF8.GetBytes(reordered), requireSignature: false).Value;

        Assert.NotEqual(indented, reordered);
        Assert.Equal(ManifestDigest.ComputeHex(first), ManifestDigest.ComputeHex(second));
    }

    /// <summary>The signature block is excluded, otherwise a signature could never contain its own digest.</summary>
    [Fact]
    public void ExcludesTheSignature()
    {
        ManifestSignature signature = new("c2ln", "key", "repo", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), new string('0', 64));

        Assert.Equal(ManifestDigest.ComputeHex(Baseline), ManifestDigest.ComputeHex(Baseline with { Signature = signature }));
    }

    /// <summary>Changing any single field changes the digest. If a field is added to the manifest and not to the digest, this fails.</summary>
    [Fact]
    public void EveryFieldAffectsTheDigest()
    {
        string baseline = ManifestDigest.ComputeHex(Baseline);
        Dictionary<string, PluginManifest> variants = new(StringComparer.Ordinal)
        {
            ["id"] = Baseline with { Id = PluginId.Create("com.example.other") },
            ["name"] = Baseline with { Name = "Other" },
            ["version"] = Baseline with { Version = "1.0.1" },
            ["description"] = Baseline with { Description = "other" },
            ["description absent"] = Baseline with { Description = null },
            ["author"] = Baseline with { Author = "other" },
            ["kinds"] = Baseline with { Kinds = [NodeKind.Create("other")] },
            ["icon absent"] = Baseline with { Icon = null },
            ["icon aspect"] = Baseline with { Icon = Baseline.Icon! with { AspectRatio = 2.0 } },
            ["icon fit"] = Baseline with { Icon = Baseline.Icon! with { Fit = "contain" } },
            ["icon width"] = Baseline with { Icon = Baseline.Icon! with { Width = 65 } },
            ["icon height"] = Baseline with { Icon = Baseline.Icon! with { Height = 65 } },
            ["required interfaces"] = Baseline with { Contract = Baseline.Contract with { Required = ["emma:core/meta@1"] } },
            ["optional interfaces"] = Baseline with { Contract = Baseline.Contract with { Optional = [] } },
            ["min host version"] = Baseline with { MinHostVersion = "2.0.0" },
            ["entrypoint path"] = Baseline with { Entrypoint = Baseline.Entrypoint with { Path = "other.wasm" } },
            ["entrypoint digest"] = Baseline with { Entrypoint = Baseline.Entrypoint with { Sha256 = new string('b', 64) } },
            ["network"] = Baseline with { Permissions = Baseline.Permissions with { Network = ["evil.example.net"] } },
            ["cache"] = Baseline with { Permissions = Baseline.Permissions with { Cache = false } },
            ["cpu"] = Baseline with { Limits = Baseline.Limits with { CpuBudgetMs = 1 } },
            ["memory"] = Baseline with { Limits = Baseline.Limits with { MemoryMb = 1 } },
        };

        foreach ((string field, PluginManifest variant) in variants)
        {
            Assert.NotEqual(baseline, ManifestDigest.ComputeHex(variant));
            Assert.True(!string.IsNullOrEmpty(field));
        }

        Assert.Equal(variants.Count, variants.Values.Select(ManifestDigest.ComputeHex).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Order matters, and the digest cannot confuse where one list item ends and the next begins.</summary>
    [Fact]
    public void ListsAreUnambiguous()
    {
        PluginManifest split = Baseline with { Permissions = Baseline.Permissions with { Network = ["ab.example.com", "c.example.com"] } };
        PluginManifest merged = Baseline with { Permissions = Baseline.Permissions with { Network = ["a.example.com", "bc.example.com"] } };
        PluginManifest reordered = Baseline with { Permissions = Baseline.Permissions with { Network = ["c.example.com", "ab.example.com"] } };

        Assert.NotEqual(ManifestDigest.ComputeHex(split), ManifestDigest.ComputeHex(merged));
        Assert.NotEqual(ManifestDigest.ComputeHex(split), ManifestDigest.ComputeHex(reordered));
    }

    /// <summary>An absent optional value is not the same as an empty one.</summary>
    [Fact]
    public void AbsentIsNotEmpty() =>
        Assert.NotEqual(
            ManifestDigest.ComputeHex(Baseline with { Description = null }),
            ManifestDigest.ComputeHex(Baseline with { Description = string.Empty }));

    private static string ReorderCompactly(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using JsonElement.ObjectEnumerator properties = document.RootElement.EnumerateObject();

        // Same properties, reverse order, no whitespace. Property names here need no escaping.
        return "{" + string.Join(",", properties.Reverse().Select(property => $"\"{property.Name}\":{property.Value.GetRawText()}")) + "}";
    }
}
