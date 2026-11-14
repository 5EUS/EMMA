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
        if (topLevel.Count == 0)
        {
            root.AddFirst(new XElement(baseName, registerBase(Directory(baseUri))));
        }
        else
        {
            // Only the first applies when a manifest lists several; the rest are alternates for the same media.
            XElement first = topLevel[0];
            Uri target = Uri.TryCreate(baseUri, first.Value.Trim(), out Uri? resolved) ? resolved : baseUri;
            first.Value = RelativeTo(target, registerBase);
            foreach (XElement alternate in topLevel.Skip(1))
            {
                alternate.Remove();
            }
        }

        foreach (XElement nested in root.Descendants(baseName).Where(element => element.Parent != root))
        {
            if (Uri.TryCreate(nested.Value.Trim(), UriKind.Absolute, out Uri? absolute))
            {
                nested.Value = RelativeTo(absolute, registerBase);
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
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
