namespace EMMA.PluginTemplate.Services;

public sealed class PlaceholderModels
{
    public sealed record ApiResponse(List<ApiItem> Data);

    public sealed record ApiItem(string Id, string Title);
}
