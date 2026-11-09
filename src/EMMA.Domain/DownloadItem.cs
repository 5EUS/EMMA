namespace EMMA.Domain;

/// <summary>One leaf of a download job: the node whose asset is stored, and where it stands.</summary>
/// <param name="JobId">The job it belongs to.</param>
/// <param name="Position">Where the leaf comes in the job, in the order the node tree lists its leaves.</param>
/// <param name="Node">The leaf node: the one whose kind declares the job's role.</param>
/// <param name="Role">The asset role, the job's.</param>
/// <param name="File">The name of the stored file, or <see langword="null"/> while the leaf is not stored.</param>
/// <param name="Mime">The type the plugin declared for the stored asset.</param>
/// <param name="Bytes">The size of the stored asset; 0 while it is not stored.</param>
/// <param name="ErrorMessage">Why the last attempt failed, or <see langword="null"/>.</param>
public sealed record DownloadItem(
    Guid JobId,
    int Position,
    NodeRef Node,
    AssetRole Role,
    string? File,
    string? Mime,
    long Bytes,
    string? ErrorMessage)
{
    /// <summary>Gets a value indicating whether the leaf's asset is stored.</summary>
    public bool IsStored => this.File is not null;
}
