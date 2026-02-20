namespace EMMA.Domain;

/// <summary>
/// Capability categories enforced by the policy engine.
/// </summary>
public enum CapabilityKind
{
    Network,
    FileRead,
    FileWrite,
    Cache
}

/// <summary>
/// Capability request payload for policy evaluation.
/// </summary>
public sealed record CapabilityRequest(CapabilityKind Kind, string? Target);

/// <summary>
/// Policy decision indicating allow/deny plus an optional reason.
/// </summary>
public sealed record CapabilityDecision(bool Allowed, string? Reason);

/// <summary>
/// Minimal capability policy with allow lists for network and file access.
/// </summary>
public sealed class CapabilityPolicy
{
    private readonly HashSet<string> _allowedNetworkDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedReadPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedWritePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Indicates whether cache access is allowed.
    /// </summary>
    public bool CacheAllowed { get; private set; }

    public IReadOnlyCollection<string> AllowedNetworkDomains => _allowedNetworkDomains;
    public IReadOnlyCollection<string> AllowedReadPaths => _allowedReadPaths;
    public IReadOnlyCollection<string> AllowedWritePaths => _allowedWritePaths;

    /// <summary>
    /// Adds an allowed network domain.
    /// </summary>
    public void AllowNetworkDomain(string domain)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            _allowedNetworkDomains.Add(domain.Trim());
        }
    }

    /// <summary>
    /// Adds an allowed read path prefix.
    /// </summary>
    public void AllowReadPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _allowedReadPaths.Add(path.Trim());
        }
    }

    /// <summary>
    /// Adds an allowed write path prefix.
    /// </summary>
    public void AllowWritePath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _allowedWritePaths.Add(path.Trim());
        }
    }

    /// <summary>
    /// Enables or disables cache access.
    /// </summary>
    public void AllowCache(bool allowed) => CacheAllowed = allowed;

    /// <summary>
    /// Evaluates a capability request against current policy rules.
    /// </summary>
    public CapabilityDecision Evaluate(CapabilityRequest request)
    {
        if (request.Kind == CapabilityKind.Cache)
        {
            return CacheAllowed
                ? new CapabilityDecision(true, null)
                : new CapabilityDecision(false, "Cache access denied.");
        }

        if (request.Kind == CapabilityKind.Network)
        {
            return IsNetworkAllowed(request.Target)
                ? new CapabilityDecision(true, null)
                : new CapabilityDecision(false, "Network access denied.");
        }

        return IsPathAllowed(request.Kind, request.Target)
            ? new CapabilityDecision(true, null)
            : new CapabilityDecision(false, "File access denied.");
    }

    private bool IsNetworkAllowed(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return _allowedNetworkDomains.Contains(target.Trim());
    }

    private bool IsPathAllowed(CapabilityKind kind, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var normalized = target.Trim();
        var allowList = kind == CapabilityKind.FileRead ? _allowedReadPaths : _allowedWritePaths;

        foreach (var allowed in allowList)
        {
            if (normalized.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
