namespace EMMA.PluginHost.Instances;

/// <summary>The optional interfaces beyond the base contract that an instance may offer, by interface id.</summary>
public static class PluginExtensions
{
    /// <summary>Search suggestions and result enrichment (<c>emma:core/search-assist@1</c>).</summary>
    public const string SearchAssist = "emma:core/search-assist@1";
}
