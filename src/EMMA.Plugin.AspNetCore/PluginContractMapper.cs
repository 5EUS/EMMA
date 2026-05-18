using EMMA.Contracts.Plugins;
using EMMA.Plugin.Common;

namespace EMMA.Plugin.AspNetCore;

/// <summary>
/// Maps common plugin SDK models onto EMMA contract types.
/// </summary>
public static class PluginContractMapper
{
    /// <summary>
    /// Appends plugin metadata items to a contract media summary.
    /// </summary>
    public static void AddMetadata(MediaSummary target, IEnumerable<MetadataItem>? metadata)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (metadata is null)
        {
            return;
        }

        foreach (var item in metadata)
        {
            target.Metadata.Add(new KeyValue { Key = item.key, Value = item.value });
        }
    }

    /// <summary>
    /// Maps a plugin search item to a contract media summary.
    /// </summary>
    public static MediaSummary ToMediaSummary(SearchItem item, IEnumerable<MetadataItem>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var result = new MediaSummary
        {
            Id = item.id,
            Source = item.source,
            Title = item.title,
            MediaType = item.mediaType,
            ThumbnailUrl = item.thumbnailUrl ?? string.Empty,
            Description = item.description ?? string.Empty,
        };

        AddMetadata(result, metadata ?? item.metadata);
        return result;
    }

    /// <summary>
    /// Clones a contract media summary and appends additional metadata.
    /// </summary>
    public static MediaSummary CloneMediaSummary(MediaSummary item, IEnumerable<MetadataItem>? additionalMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var result = new MediaSummary
        {
            Id = item.Id,
            Source = item.Source,
            Title = item.Title,
            MediaType = item.MediaType,
            ThumbnailUrl = item.ThumbnailUrl,
            Description = item.Description,
        };

        result.Metadata.AddRange(item.Metadata);
        AddMetadata(result, additionalMetadata);
        return result;
    }
}