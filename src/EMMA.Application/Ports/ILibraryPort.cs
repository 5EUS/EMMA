using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for user library entries.
/// </summary>
public interface ILibraryPort
{
    Task CreateLibraryAsync(string userId, string libraryName, CancellationToken cancellationToken);
    Task NormalizeLegacyDefaultLibraryAsync(string canonicalUserId, CancellationToken cancellationToken);
    Task UpsertAsync(LibraryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryEntry>> GetLibraryAsync(string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListLibrariesAsync(CancellationToken cancellationToken);
    Task RemoveAsync(string userId, MediaId mediaId, CancellationToken cancellationToken);
}
