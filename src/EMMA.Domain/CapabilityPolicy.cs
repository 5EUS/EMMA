namespace EMMA.Domain;

/// <summary>
/// What one plugin may ask of the host, derived from its signed manifest. Everything
/// not granted is denied. There is deliberately no filesystem capability: a Wasm
/// plugin has no filesystem.
/// </summary>
public sealed class CapabilityPolicy
{
    private readonly string[] exactHosts;
    private readonly string[] wildcardSuffixes;

    /// <summary>Initializes a new instance of the <see cref="CapabilityPolicy"/> class.</summary>
    /// <param name="networkHosts">Exact host names (case-insensitive), or <c>*.example.com</c> wildcards.</param>
    /// <param name="cacheAllowed">Whether the plugin may use the host cache.</param>
    public CapabilityPolicy(IEnumerable<string> networkHosts, bool cacheAllowed)
    {
        List<string> exact = [];
        List<string> wildcards = [];
        foreach (string entry in networkHosts)
        {
            string normalized = entry.Trim().ToUpperInvariant();
            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                wildcards.Add(normalized[1..]);
            }
            else if (normalized.Length > 0)
            {
                exact.Add(normalized);
            }
        }

        this.exactHosts = [.. exact];
        this.wildcardSuffixes = [.. wildcards];
        this.CacheAllowed = cacheAllowed;
    }

    /// <summary>Gets a policy that denies everything.</summary>
    public static CapabilityPolicy DenyAll { get; } = new([], false);

    /// <summary>Gets a value indicating whether the plugin may use the host cache.</summary>
    public bool CacheAllowed { get; }

    /// <summary>
    /// Decides whether the plugin may reach a URL. Only http and https are ever allowed.
    /// A <c>*.example.com</c> entry matches subdomains at any depth but not the apex
    /// <c>example.com</c>, and wildcards never match IP literals.
    /// </summary>
    /// <param name="url">The URL the plugin wants to reach.</param>
    /// <returns><see langword="true"/> when the host is allowed.</returns>
    public bool AllowsNetwork(Uri url)
    {
        if (!url.IsAbsoluteUri
            || !(string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            return false;
        }

        string host = url.IdnHost.ToUpperInvariant();
        if (Array.Exists(this.exactHosts, known => string.Equals(known, host, StringComparison.Ordinal)))
        {
            return true;
        }

        return url.HostNameType is UriHostNameType.Dns
            && Array.Exists(this.wildcardSuffixes, suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }
}
