using EMMA.Application.Streaming;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="HlsPlaylist"/>: what is found in a playlist, how addresses are rewritten, and how a playlist is repaired.</summary>
public sealed class HlsPlaylistTests
{
    private const string Master = @"#EXTM3U
#EXT-X-VERSION:6
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=""aud"",NAME=""en"",URI=""audio/en.m3u8""
#EXT-X-STREAM-INF:BANDWIDTH=800000,AUDIO=""aud""
low/index.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=2400000,AUDIO=""aud""
https://other.example.net/high/index.m3u8
";

    private const string Media = @"#EXTM3U
#EXT-X-TARGETDURATION:6
#EXT-X-MEDIA-SEQUENCE:10
#EXT-X-KEY:METHOD=AES-128,URI=""key.bin""
#EXT-X-MAP:URI=""init.mp4""
#EXTINF:6.0,
seg1.ts
#EXTINF:6.0,
seg2.ts
#EXTINF:6.0,
seg3.ts
#EXT-X-ENDLIST
";

    private static readonly Uri Origin = TestData.Url("cdn.example.com", "/v/master.m3u8");

    /// <summary>Everything a playlist points at is listed once, resolved, and told apart as a playlist or a file.</summary>
    [Fact]
    public void ReferencesAreResolvedAndTypedAsPlaylistsOrFiles()
    {
        IReadOnlyList<HlsReference> master = HlsPlaylist.References(Master, Origin);
        IReadOnlyList<HlsReference> media = HlsPlaylist.References(Media, Origin);

        Assert.Equal(
            [
                ("https://cdn.example.com/v/audio/en.m3u8", true),
                ("https://cdn.example.com/v/low/index.m3u8", true),
                ("https://other.example.net/high/index.m3u8", true),
            ],
            master.Select(reference => (reference.Url.AbsoluteUri, reference.IsPlaylist)));
        Assert.Equal(
            ["key.bin", "init.mp4", "seg1.ts", "seg2.ts", "seg3.ts"],
            media.Select(reference => reference.Url.Segments[^1]));
        Assert.All(media, reference => Assert.False(reference.IsPlaylist));
    }

    /// <summary>Rewriting swaps every address for what the map says, in tags and on their own lines, and touches nothing else.</summary>
    [Fact]
    public void TransformSwapsEveryAddressAndKeepsTheRest()
    {
        int next = 0;
        string result = HlsPlaylist.Transform(Media, Origin, _ => $"p{next++}", available: null);

        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("#EXT-X-KEY:METHOD=AES-128,URI=\"p0\"", lines[3]);
        Assert.Equal("#EXT-X-MAP:URI=\"p1\"", lines[4]);
        Assert.Equal(["p2", "p3", "p4"], lines.Where(line => line.StartsWith('p')));
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:10", lines);
        Assert.Contains("#EXT-X-ENDLIST", lines);
        Assert.Equal(3, lines.Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));
    }

    /// <summary>A variant's address is the line after its tag, and a rendition's is in the tag; both are playlists.</summary>
    [Fact]
    public void TransformRewritesVariantsAndRenditions()
    {
        string result = HlsPlaylist.Transform(Master, Origin, reference => reference.IsPlaylist ? "list" : "file", available: null);

        Assert.Equal(3, result.Split('\n').Count(line => line.Contains("list", StringComparison.Ordinal)));
        Assert.Contains("URI=\"list\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("example", result, StringComparison.Ordinal);
    }

    /// <summary>An address that is not on the web (a FairPlay key, a data URI) is left for the player.</summary>
    [Fact]
    public void AddressesThatAreNotWebAddressesAreLeftAlone()
    {
        const string text = "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key-id\"\n#EXTINF:4,\ndata:text/plain,x\n";

        string result = HlsPlaylist.Transform(text, Origin, _ => "mapped", available: null);

        Assert.Contains("skd://key-id", result, StringComparison.Ordinal);
        Assert.Contains("data:text/plain,x", result, StringComparison.Ordinal);
        Assert.DoesNotContain("mapped", result, StringComparison.Ordinal);
    }

    /// <summary>Windows line endings and a leading byte-order mark are read like any other playlist.</summary>
    [Fact]
    public void LineEndingsDoNotMatter()
    {
        const string text = "#EXTM3U\r\n#EXTINF:4,\r\nseg.ts\r\n";
        string withMark = (char)0xFEFF + text;

        Assert.True(HlsPlaylist.Looks(withMark));
        Assert.Equal("#EXTM3U\n#EXTINF:4,\nm\n", HlsPlaylist.Transform(text, Origin, _ => "m", available: null));
    }

    /// <summary>A repaired playlist keeps the segments that can be served, marks each gap, and ends.</summary>
    [Fact]
    public void RepairKeepsWhatIsAvailableMarksGapsAndEnds()
    {
        string noEnd = Media.Replace("#EXT-X-ENDLIST\n", string.Empty, StringComparison.Ordinal);
        string result = HlsPlaylist.Transform(noEnd, Origin, reference => reference.Url.Segments[^1], reference => reference.Url.Segments[^1] is not "seg2.ts");

        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int seg1 = Array.IndexOf(lines, "seg1.ts");
        int seg3 = Array.IndexOf(lines, "seg3.ts");
        Assert.DoesNotContain("seg2.ts", lines);
        Assert.Equal(2, lines.Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));
        Assert.Equal("#EXT-X-DISCONTINUITY", lines[seg3 - 2]);
        Assert.Equal("#EXTINF:6.0,", lines[seg3 - 1]);
        Assert.True(seg1 < seg3);
        Assert.Equal("#EXT-X-ENDLIST", lines[^1]);
    }

    /// <summary>The key and map a segment needs are never dropped, even when the cache does not hold them.</summary>
    [Fact]
    public void RepairNeverDropsKeysOrMaps()
    {
        string result = HlsPlaylist.Transform(Media, Origin, reference => reference.Url.Segments[^1], reference => reference.Url.Segments[^1].StartsWith("seg", StringComparison.Ordinal));

        Assert.Contains("URI=\"key.bin\"", result, StringComparison.Ordinal);
        Assert.Contains("URI=\"init.mp4\"", result, StringComparison.Ordinal);
    }

    /// <summary>A master playlist is cut down to the variants and renditions that can be played, and is not given an end.</summary>
    [Fact]
    public void RepairCutsAMasterToThePlayableVariants()
    {
        string result = HlsPlaylist.Transform(Master, Origin, reference => reference.Url.AbsoluteUri, reference => string.Equals(reference.Url.Host, "cdn.example.com", StringComparison.Ordinal) && reference.Url.AbsolutePath.Contains("low", StringComparison.Ordinal));

        Assert.Contains("low/index.m3u8", result, StringComparison.Ordinal);
        Assert.DoesNotContain("other.example.net", result, StringComparison.Ordinal);
        Assert.DoesNotContain("audio/en.m3u8", result, StringComparison.Ordinal);
        Assert.Equal(1, result.Split('\n').Count(line => line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)));
        Assert.DoesNotContain("ENDLIST", result, StringComparison.Ordinal);
    }
}
