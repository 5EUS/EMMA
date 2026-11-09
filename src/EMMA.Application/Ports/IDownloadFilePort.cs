namespace EMMA.Application.Ports;

/// <summary>
/// Keeps the bytes of downloaded assets. Unlike the asset cache this never evicts: a file stays until the
/// download that stored it is deleted. Names are the opaque names <c>IDownloadPort</c> records.
/// </summary>
public interface IDownloadFilePort
{
    /// <summary>Stores bytes under a name, replacing any earlier file of that name. A reader never sees half a file.</summary>
    /// <param name="name">The file name: lower-case hexadecimal.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is stored.</returns>
    Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Reads a stored file.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The content, or <see langword="null"/> when there is no such file (a user may have deleted it).</returns>
    Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Deletes stored files. A name that does not exist is ignored.</summary>
    /// <param name="names">The file names.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the files are gone.</returns>
    Task DeleteAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);
}
