namespace EMMA.Infrastructure.Http;

/// <summary>
/// Connection settings for the plugin host pipeline endpoints.
/// </summary>
public sealed record PluginHostClientOptions
{
    public string BaseUrl { get; init; } = "http://localhost:5223";
    public string? PluginId { get; init; }
}
