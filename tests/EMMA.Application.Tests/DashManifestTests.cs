using EMMA.Application.Streaming;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="DashManifest"/>.</summary>
public sealed class DashManifestTests
{
    private static readonly Uri Origin = TestData.Url("cdn.example.com", "/show/ep1/manifest.mpd");

    /// <summary>A manifest with no base gets one that points at its own directory.</summary>
    [Fact]
    public void AManifestWithoutABaseGetsOneForItsOwnDirectory()
    {
        List<Uri> registered = [];
        string? result = DashManifest.Rewrite(
            "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"><Period/></MPD>",
            Origin,
            directory =>
            {
                registered.Add(directory);
                return "3";
            });

        Assert.Equal(["https://cdn.example.com/show/ep1/"], registered.Select(uri => uri.AbsoluteUri));
        Assert.Contains("<BaseURL>3</BaseURL>", result, StringComparison.Ordinal);
    }

    /// <summary>A relative base is resolved against the manifest, and extra bases (alternates) are dropped.</summary>
    [Fact]
    public void ARelativeBaseIsResolvedAndAlternatesAreDropped()
    {
        List<Uri> registered = [];
        string? result = DashManifest.Rewrite(
            "<MPD><BaseURL>../media/</BaseURL><BaseURL>https://mirror.example.net/media/</BaseURL></MPD>",
            Origin,
            directory =>
            {
                registered.Add(directory);
                return "7";
            });

        Assert.Equal(["https://cdn.example.com/show/media/"], registered.Select(uri => uri.AbsoluteUri));
        Assert.Contains("<BaseURL>7</BaseURL>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("mirror", result, StringComparison.Ordinal);
    }

    /// <summary>A file named by an absolute base further down keeps its name and gets its directory served by the host.</summary>
    [Fact]
    public void AnAbsoluteBaseOnARepresentationKeepsItsFileName()
    {
        string? result = DashManifest.Rewrite(
            "<MPD><Period><AdaptationSet><Representation><BaseURL>https://files.example.org/v/video.mp4?token=a</BaseURL></Representation></AdaptationSet></Period></MPD>",
            Origin,
            directory => string.Equals(directory.Host, "files.example.org", StringComparison.Ordinal) ? "9/" : "0/");

        Assert.Contains("<BaseURL>9/video.mp4?token=a</BaseURL>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("files.example.org", result, StringComparison.Ordinal);
    }

    /// <summary>A relative base further down is left alone: it follows its parent, which is served by the host.</summary>
    [Fact]
    public void ARelativeBaseOnARepresentationIsLeftAlone()
    {
        string? result = DashManifest.Rewrite(
            "<MPD><Period><BaseURL>video/</BaseURL></Period></MPD>",
            Origin,
            _ => "1/");

        Assert.Contains("<BaseURL>video/</BaseURL>", result, StringComparison.Ordinal);
    }

    /// <summary>Text that is not a manifest, or that declares an external entity, is refused rather than guessed at.</summary>
    [Fact]
    public void ThingsThatAreNotManifestsAreRefused()
    {
        Assert.Null(DashManifest.Rewrite("<html/>", Origin, _ => "x"));
        Assert.Null(DashManifest.Rewrite("not xml", Origin, _ => "x"));
        Assert.Null(DashManifest.Rewrite("<!DOCTYPE MPD [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><MPD>&e;</MPD>", Origin, _ => "x"));
        Assert.True(DashManifest.Looks("<?xml version=\"1.0\"?><MPD/>"));
    }
}
