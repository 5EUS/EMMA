using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps the media tree to its wire form.</summary>
public static class NodeMapper
{
    /// <summary>Maps a node.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The wire form.</returns>
    public static NodeDto ToDto(Node node) => new(
        node.Ref.ToString(),
        node.Kind.Value,
        node.Title,
        node.Parent?.ToString(),
        node.Thumbnail?.AbsoluteUri,
        PropMapper.ToDtos(node.Props));

    /// <summary>Maps a page of nodes.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The wire form.</returns>
    public static NodePageResponse ToDto(NodePage page) => new(
        [.. page.Items.Select(ToDto)],
        page.NextCursor,
        page.Total,
        [.. page.Facets.Select(facet => new FacetDto(
            facet.FilterId,
            [.. facet.Counts.Select(count => new FacetCountDto(count.Value, count.Label, count.Count))]))]);

    /// <summary>Maps what refreshing a library found.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The wire form.</returns>
    public static RefreshResponse ToDto(RefreshReport report) => new(
        report.Checked,
        [.. report.Updated.Select(update => new NodeUpdateDto(ToDto(update.Node), [.. update.NewChildren.Select(ToDto)]))],
        [.. report.Failed.Select(failure => new RefreshFailureDto(failure.Node.ToString(), ApiErrors.ToResponse(failure.Error).Error))]);

    /// <summary>Maps a search request, which the pipeline then validates against the plugin's declaration.</summary>
    /// <param name="request">The request body.</param>
    /// <returns>The query, or why a field is invalid.</returns>
    public static Outcome<Query> ToQuery(SearchRequest request)
    {
        List<NodeKind> kinds = [];
        foreach (string kind in request.Kinds ?? [])
        {
            if (!NodeKind.TryCreate(kind, out NodeKind parsed))
            {
                return Outcome.Failure<Query>(ErrorCode.InvalidArgument, "A kind in 'kinds' is not valid.");
            }

            kinds.Add(parsed);
        }

        List<FilterValue> filters = [];
        foreach (FilterValueDto filter in request.Filters ?? [])
        {
            if (string.IsNullOrWhiteSpace(filter.Id) || filter.Values is null)
            {
                return Outcome.Failure<Query>(ErrorCode.InvalidArgument, "Every filter needs an 'id' and 'values'.");
            }

            filters.Add(new FilterValue(filter.Id, filter.Values));
        }

        return Outcome.Success(new Query(request.Text ?? string.Empty, kinds, filters, request.Sort, request.Cursor, request.Limit));
    }
}
