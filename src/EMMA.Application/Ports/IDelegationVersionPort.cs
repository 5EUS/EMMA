namespace EMMA.Application.Ports;

/// <summary>
/// Remembers the highest delegation-file version the host has accepted for each repository,
/// so an old, validly signed file that still lists a revoked key as active cannot be replayed.
/// </summary>
public interface IDelegationVersionPort
{
    /// <summary>
    /// Records a version unless it is lower than one already accepted. The check and the
    /// write are one atomic step.
    /// </summary>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="version">The delegation file's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the version is current (equal or newer); <see langword="false"/> when it is a rollback.</returns>
    Task<bool> TryAdvanceAsync(string repositoryId, long version, CancellationToken cancellationToken);
}
