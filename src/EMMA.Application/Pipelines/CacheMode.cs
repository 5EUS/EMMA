namespace EMMA.Application.Pipelines;

/// <summary>Whether a read may be answered from what the host already stored.</summary>
public enum CacheMode
{
    /// <summary>Answer from the host's stored copy when it has one; ask the plugin only for what is missing.</summary>
    Use,

    /// <summary>Always ask the plugin and replace the stored copy.</summary>
    Refresh,
}
