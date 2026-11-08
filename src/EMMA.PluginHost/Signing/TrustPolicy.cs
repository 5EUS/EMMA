namespace EMMA.PluginHost.Signing;

/// <summary>
/// Which repositories the host trusts and whether unsigned plugins may run. The host builds this
/// from its own configuration and passes it in. Nothing here is read from the environment, so a
/// plugin, a package or a process variable cannot switch signature checks off.
/// </summary>
/// <param name="Repositories">Trusted repositories by id.</param>
/// <param name="RequireSigned">When <see langword="true"/> (the default for a release host) an unsigned plugin is refused.</param>
public sealed record TrustPolicy(IReadOnlyDictionary<string, TrustedRepository> Repositories, bool RequireSigned)
{
    /// <summary>Gets a strict policy that trusts no repository, so nothing can be installed.</summary>
    public static TrustPolicy TrustNothing { get; } = new(new Dictionary<string, TrustedRepository>(StringComparer.Ordinal), true);

    /// <summary>Builds a policy from a list of repositories.</summary>
    /// <param name="repositories">The trusted repositories.</param>
    /// <param name="requireSigned">Whether unsigned plugins are refused.</param>
    /// <returns>The policy.</returns>
    public static TrustPolicy Create(IEnumerable<TrustedRepository> repositories, bool requireSigned) =>
        new(repositories.ToDictionary(repository => repository.Id, StringComparer.Ordinal), requireSigned);
}
