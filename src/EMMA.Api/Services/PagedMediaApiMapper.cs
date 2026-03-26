using EMMA.Contracts.Api.V1;
using EMMA.Domain;

namespace EMMA.Api.Services;

internal static class PagedMediaApiMapper
{
    public static ApiMediaSummary MapSummary(MediaSummary summary)
    {
        return new ApiMediaSummary
        {
            Id = summary.Id.Value,
            Source = summary.SourceId,
            Title = summary.Title,
            MediaType = MapMediaType(summary.MediaType),
            ThumbnailUrl = summary.ThumbnailUrl ?? string.Empty
        };
    }

    public static ApiMediaChapter MapChapter(MediaChapter chapter)
    {
        return new ApiMediaChapter
        {
            Id = chapter.ChapterId,
            Number = chapter.Number,
            Title = chapter.Title
        };
    }

    public static ApiMediaPage MapPage(MediaPage page)
    {
        return new ApiMediaPage
        {
            Id = page.PageId,
            Index = page.Index,
            ContentUri = page.ContentUri.ToString()
        };
    }

    public static ApiPageAsset MapAsset(MediaPageAsset asset)
    {
        return new ApiPageAsset
        {
            ContentType = asset.ContentType,
            Payload = Google.Protobuf.ByteString.CopyFrom(asset.Payload)
        };
    }

    public static ApiMediaType MapMediaType(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Video => ApiMediaType.Video,
            MediaType.Paged => ApiMediaType.Paged,
            _ => ApiMediaType.Unspecified
        };
    }

    public static ApiError CreateError(Exception ex)
    {
        var error = ex switch
        {
            KeyNotFoundException => new ApiError { Code = "not_found" },
            TimeoutException => new ApiError { Code = "timeout" },
            OperationCanceledException => new ApiError { Code = "cancelled" },
            InvalidOperationException => new ApiError { Code = "invalid_request" },
            _ => new ApiError { Code = "upstream_failure" }
        };

        error.Message = string.IsNullOrWhiteSpace(ex.Message) ? "Request failed." : ex.Message;
        return error;
    }

    public static ApiError InvalidRequest(string message)
    {
        return new ApiError
        {
            Code = "invalid_request",
            Message = message
        };
    }
}
