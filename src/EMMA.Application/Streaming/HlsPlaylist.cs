using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace EMMA.Application.Streaming;

/// <summary>
/// Reads and rewrites HLS playlists (RFC 8216). The host serves a stream to a player through a local address, so every
/// address a playlist holds is swapped for one the host can answer, and where the player cannot reach the host's cache
/// of segments the playlist can be repaired to hold only what is there.
/// </summary>
internal static partial class HlsPlaylist
{
    private const string EndList = "#EXT-X-ENDLIST";
    private const string Discontinuity = "#EXT-X-DISCONTINUITY";

    // Tags that describe the URI line that follows them, so they stay with it when it is dropped.
    private static readonly FrozenSet<string> TagsOfNextUri = new[]
    {
        "EXTINF", "EXT-X-BYTERANGE", "EXT-X-PROGRAM-DATE-TIME", "EXT-X-GAP", "EXT-X-STREAM-INF",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Tags whose URI attribute names another playlist.
    private static readonly FrozenSet<string> PlaylistTags = new[]
    {
        "EXT-X-MEDIA", "EXT-X-I-FRAME-STREAM-INF", "EXT-X-RENDITION-REPORT",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Tags that have a URI attribute naming a file the player needs.
    private static readonly FrozenSet<string> FileTags = new[]
    {
        "EXT-X-KEY", "EXT-X-SESSION-KEY", "EXT-X-MAP", "EXT-X-PART", "EXT-X-PRELOAD-HINT",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Says whether some text is an HLS playlist.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when it starts with <c>#EXTM3U</c>.</returns>
    public static bool Looks(string text) => text.AsSpan().TrimStart('﻿').StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <summary>Lists what a playlist points at.</summary>
    /// <param name="text">The playlist.</param>
    /// <param name="baseUri">The address the playlist was fetched from, for relative references.</param>
    /// <returns>The references in the order they appear, resolved and limited to web addresses.</returns>
    public static IReadOnlyList<HlsReference> References(string text, Uri baseUri)
    {
        List<HlsReference> found = [];
        _ = Transform(
            text,
            baseUri,
            reference =>
            {
                found.Add(reference);
                return string.Empty;
            },
            available: null);
        return found;
    }

    /// <summary>
    /// Rewrites a playlist: every web address in it is replaced by what <paramref name="map"/> says, and, when
    /// <paramref name="available"/> is given, whatever it refuses is dropped. A dropped segment leaves a discontinuity and a
    /// repaired playlist ends, so a player plays what is there and stops instead of waiting for what is not.
    /// </summary>
    /// <param name="text">The playlist.</param>
    /// <param name="baseUri">The address the playlist was fetched from, for relative references.</param>
    /// <param name="map">Gives the text to put where a reference was.</param>
    /// <param name="available">Says whether a segment, variant or rendition can still be served; <see langword="null"/> keeps everything.</param>
    /// <returns>The new playlist.</returns>
    public static string Transform(string text, Uri baseUri, Func<HlsReference, string> map, Func<HlsReference, bool>? available)
    {
        Rewriter rewriter = new(baseUri, map, available);
        foreach (string raw in text.Split('\n'))
        {
            rewriter.Add(raw.TrimEnd('\r'));
        }

        return rewriter.Finish();
    }

    private static string TagOf(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? line[1..] : line[1..colon];
    }

    private static bool TryResolve(Uri baseUri, string reference, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? url)
    {
        if (Uri.TryCreate(baseUri, reference, out Uri? resolved)
            && (string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(resolved.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            url = resolved;
            return true;
        }

        url = null;
        return false;
    }

    [GeneratedRegex("URI=\"(?<uri>[^\"]*)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex UriAttribute();

    /// <summary>Reads a playlist line by line, holding back the tags that belong to the address that follows them.</summary>
    private sealed class Rewriter(Uri baseUri, Func<HlsReference, string> map, Func<HlsReference, bool>? available)
    {
        private readonly List<string> output = [];
        private readonly List<string> pending = [];
        private bool variant;
        private bool master;
        private bool gap;

        public void Add(string line)
        {
            if (line.Length == 0)
            {
                return;
            }

            if (line[0] == '#')
            {
                this.AddTag(line);
            }
            else
            {
                this.AddAddress(line);
            }
        }

        public string Finish()
        {
            if (available is not null && !this.master && !this.output.Contains(EndList, StringComparer.Ordinal))
            {
                this.output.Add(EndList);
            }

            StringBuilder result = new();
            foreach (string line in this.output)
            {
                result.Append(line).Append('\n');
            }

            return result.ToString();
        }

        private void AddTag(string line)
        {
            string tag = TagOf(line);
            if (TagsOfNextUri.Contains(tag))
            {
                this.variant |= string.Equals(tag, "EXT-X-STREAM-INF", StringComparison.Ordinal);
                this.master |= this.variant;
                this.pending.Add(line);
            }
            else if (PlaylistTags.Contains(tag) || FileTags.Contains(tag))
            {
                string? rewritten = this.RewriteAttribute(line, PlaylistTags.Contains(tag));
                if (rewritten is not null)
                {
                    this.output.Add(rewritten);
                }
            }
            else
            {
                this.output.Add(line);
            }
        }

        private void AddAddress(string line)
        {
            if (!TryResolve(baseUri, line, out Uri? url))
            {
                this.Flush(line);
                this.variant = false;
                return;
            }

            HlsReference reference = new(url, this.variant);
            this.variant = false;
            if (available is not null && !available(reference))
            {
                this.pending.Clear();
                this.gap = true;
                return;
            }

            if (this.gap && !reference.IsPlaylist)
            {
                this.output.Add(Discontinuity);
                this.gap = false;
            }

            this.Flush(map(reference));
        }

        private void Flush(string address)
        {
            this.output.AddRange(this.pending);
            this.output.Add(address);
            this.pending.Clear();
        }

        /// <summary>Rewrites the <c>URI</c> attribute of a tag line, or returns <see langword="null"/> when the line must go.</summary>
        private string? RewriteAttribute(string line, bool isPlaylist)
        {
            Match match = UriAttribute().Match(line);
            if (!match.Success || !TryResolve(baseUri, match.Groups["uri"].Value, out Uri? url))
            {
                return line;
            }

            HlsReference reference = new(url, isPlaylist);

            // A rendition that cannot be played is left out; a key or map is never dropped, because the segments need it.
            if (isPlaylist && available is not null && !available(reference))
            {
                return null;
            }

            return $"{line[..match.Index]}URI=\"{map(reference)}\"{line[(match.Index + match.Length)..]}";
        }
    }
}
