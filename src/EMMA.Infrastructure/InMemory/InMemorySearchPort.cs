using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory search adapter that filters demo media by title.
/// </summary>
public sealed class InMemorySearchPort(InMemoryMediaStore store) : IMediaSearchPort
{
    private readonly InMemoryMediaStore _store = store;

    /// <summary>
    /// Searches the in-memory catalog by title.
    /// </summary>
    public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = query.Trim();
        var results = _store.Media
            .Where(media => media.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<MediaSummary>>(results);
    }
}
