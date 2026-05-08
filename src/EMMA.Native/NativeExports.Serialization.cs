using System.Globalization;
using System.Text;

using EMMA.Domain;

namespace EMMA.Native;

public static partial class NativeExports
{
    private static string BuildChaptersJson(IReadOnlyList<MediaChapter> chapters)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < chapters.Count; i++)
        {
            var item = chapters[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", item.ChapterId ?? string.Empty);
            sb.Append(',');
            AppendJsonNumberProperty(sb, "number", item.Number);
            sb.Append(',');
            AppendJsonProperty(sb, "title", item.Title ?? string.Empty);
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string BuildPageJson(MediaPage page)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "id", page.PageId ?? string.Empty);
        sb.Append(',');
        AppendJsonNumberProperty(sb, "index", page.Index);
        sb.Append(',');
        AppendJsonProperty(sb, "contentUri", page.ContentUri.ToString());
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPagesJson(MediaPagesResult result)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonString(sb, "pages");
        sb.Append(':');
        sb.Append('[');

        for (var i = 0; i < result.Pages.Count; i++)
        {
            var page = result.Pages[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", page.PageId ?? string.Empty);
            sb.Append(',');
            AppendJsonNumberProperty(sb, "index", page.Index);
            sb.Append(',');
            AppendJsonProperty(sb, "contentUri", page.ContentUri.ToString());
            sb.Append('}');
        }

        sb.Append(']');
        sb.Append(',');
        AppendJsonString(sb, "reachedEnd");
        sb.Append(':');
        sb.Append(result.ReachedEnd ? "true" : "false");
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPageAssetJson(MediaPageAsset asset)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "contentType", asset.ContentType ?? "application/octet-stream");
        sb.Append(',');
        AppendJsonProperty(sb, "payload", Convert.ToBase64String(asset.Payload ?? Array.Empty<byte>()));
        sb.Append(',');
        AppendJsonProperty(sb, "fetchedAtUtc", asset.FetchedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }
}