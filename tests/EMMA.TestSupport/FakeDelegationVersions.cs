using EMMA.Application.Ports;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IDelegationVersionPort"/> with the same accept-equal-or-newer rule as the real store.</summary>
public sealed class FakeDelegationVersions : IDelegationVersionPort
{
    private readonly Dictionary<string, long> highest = new(StringComparer.Ordinal);

    /// <summary>Gets the highest version accepted for a repository, or -1 when none.</summary>
    /// <param name="repositoryId">The repository.</param>
    /// <returns>The version.</returns>
    public long Highest(string repositoryId) => this.highest.GetValueOrDefault(repositoryId, -1);

    /// <inheritdoc/>
    public Task<bool> TryAdvanceAsync(string repositoryId, long version, CancellationToken cancellationToken)
    {
        if (version < this.Highest(repositoryId))
        {
            return Task.FromResult(false);
        }

        this.highest[repositoryId] = version;
        return Task.FromResult(true);
    }
}
