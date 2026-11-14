using System.Xml;
using System.Xml.Linq;

namespace EMMA.Application.Streaming;

/// <summary>
/// Rewrites a DASH manifest (an MPD) so a player takes its media from the host. A manifest points at media through
/// <c>BaseURL</c> elements and relative templates, so it is enough to give it one base that the host serves: every
/// address that was absolute is swapped for a path under a base, and everything relative follows its parent.
/// </summary>
internal static class DashManifest
{
    /// <summary>
    /// A stand-in for the address the host serves the manifest at. Only the shape matters: the host's directories sit
    /// beside the manifest, so a rewritten address is worked out as a reference from one stand-in directory to another.
    /// </summary>
    private static readonly Uri ProxyManifest = new("http://proxy.invalid/s/0");

    /// <summary>Says whether some text is a DASH manifest.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when it holds an <c>MPD</c> element.</returns>
    public static bool Looks(string text) => text.Contains("<MPD", StringComparison.Ordinal);

    /// <summary>Rewrites a manifest.</summary>
    /// <param name="text">The manifest.</param>
    /// <param name="baseUri">The address the manifest was fetched from.</param>
    /// <param name="registerBase">Registers a directory the host will serve and gives the relative path that reaches it.</param>
    /// <returns>The new manifest, or <see langword="null"/> when the text is not a manifest this can read.</returns>
    public static string? Rewrite(string text, Uri baseUri, Func<Uri, string> registerBase)
    {
        XDocument document;
        try
        {
            using StringReader reader = new(text);
            using XmlReader xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { Name.LocalName: "MPD" } root)
        {
            return null;
        }

        XName baseName = root.Name.Namespace + "BaseURL";
        List<XElement> topLevel = [.. root.Elements(baseName)];
        Uri originalBase;
        Uri proxyBase;
        if (topLevel.Count == 0)
        {
            string part = registerBase(Directory(baseUri));
            root.AddFirst(new XElement(baseName, part));
            originalBase = baseUri;
            proxyBase = new Uri(ProxyManifest, part);
        }
        else
        {
            // Only the first applies when a manifest lists several; the rest are alternates for the same media.
            XElement first = topLevel[0];
            Uri target = Uri.TryCreate(baseUri, first.Value.Trim(), out Uri? resolved) ? resolved : baseUri;
            string part = RelativeTo(target, registerBase);
            first.Value = part;
            foreach (XElement alternate in topLevel.Skip(1))
            {
                alternate.Remove();
            }

            originalBase = target;
            proxyBase = new Uri(ProxyManifest, part);
        }

        Descend(root, baseName, originalBase, proxyBase, registerBase);
        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Rewrites the <c>BaseURL</c> elements beneath <paramref name="parent"/>. A base is resolved against the base of
    /// its parent, so a rewritten address is made relative to the parent's rewritten base, not to the manifest: with a
    /// base of <c>1/</c> above it, the way to the host's directory 2 is <c>../2/</c>.
    /// </summary>
    private static void Descend(XElement parent, XName baseName, Uri originalBase, Uri proxyBase, Func<Uri, string> registerBase)
    {
        foreach (XElement child in parent.Elements().Where(element => element.Name != baseName))
        {
            Uri childOriginal = originalBase;
            Uri childProxy = proxyBase;
            List<XElement> bases = [.. child.Elements(baseName)];
            if (bases.Count > 0)
            {
                string value = bases[0].Value.Trim();
                Uri? target = Reach(originalBase, value);
                if (target is not null)
                {
                    string name = target.AbsoluteUri[Directory(target).AbsoluteUri.Length..];
                    Uri served = new(ProxyManifest, registerBase(Directory(target)) + name);
                    string relative = proxyBase.MakeRelativeUri(served).ToString();
                    foreach (XElement element in bases)
                    {
                        element.Value = relative;
                    }

                    childOriginal = target;
                    childProxy = served;
                }
                else if (Uri.TryCreate(originalBase, value, out Uri? original) && Uri.TryCreate(proxyBase, value, out Uri? proxy))
                {
                    // A relative base follows its parent, which the host serves: it is left as it is.
                    childOriginal = original;
                    childProxy = proxy;
                }
            }

            Descend(child, baseName, childOriginal, childProxy, registerBase);
        }
    }

    /// <summary>
    /// The address a base names when it does not simply follow its parent: an absolute http or https address, or one
    /// that starts at the root of a host (<c>/path</c> or <c>//host/path</c>). <see langword="null"/> for a plain relative
    /// path. (On Unix, <c>/path</c> also parses as an absolute <c>file:</c> address, which is not an address to fetch.)
    /// </summary>
    private static Uri? Reach(Uri parent, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)
            && (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            return absolute;
        }

        return value.StartsWith('/') && Uri.TryCreate(parent, value, out Uri? rooted) ? rooted : null;
    }

    private static string RelativeTo(Uri target, Func<Uri, string> registerBase)
    {
        string name = target.AbsoluteUri[Directory(target).AbsoluteUri.Length..];
        return registerBase(Directory(target)) + name;
    }

    private static Uri Directory(Uri url)
    {
        UriBuilder builder = new(url) { Query = string.Empty, Fragment = string.Empty };
        string path = builder.Path;
        builder.Path = path[..(path.LastIndexOf('/') + 1)];
        return builder.Uri;
    }
}
