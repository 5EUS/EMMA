namespace EMMA.Native.Requests;

/// <summary>Queues a download.</summary>
/// <param name="Ref">The node reference: a chapter, a season, a series or a single leaf.</param>
/// <param name="Role">The asset role to store.</param>
/// <param name="Hint">A preference passed to the plugin, such as a quality (<c>1080p</c>).</param>
internal sealed record DownloadEnqueueCall(
    string? Ref,
    string? Role,
    string? Hint);
