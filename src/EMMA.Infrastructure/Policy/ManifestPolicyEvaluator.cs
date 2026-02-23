using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Policy;

/// <summary>
/// Policy evaluator that enforces manifest-derived permissions and capabilities.
/// </summary>
public sealed class ManifestPolicyEvaluator : IPolicyEvaluator
{
    private readonly CapabilityPolicy _policy = new();

    public ManifestPolicyEvaluator(ManifestPolicyDefinition definition)
    {
        _policy.AllowCache(definition.CacheAllowed);

        if (definition.Domains is not null)
        {
            foreach (var domain in definition.Domains)
            {
                _policy.AllowNetworkDomain(domain);
            }
        }

        if (definition.Paths is not null)
        {
            var allowRead = HasFileCapability(definition.FileSystem, "read")
                || HasFileCapability(definition.FileSystem, "write");
            var allowWrite = HasFileCapability(definition.FileSystem, "write");

            foreach (var path in definition.Paths)
            {
                if (allowRead)
                {
                    _policy.AllowReadPath(path);
                }

                if (allowWrite)
                {
                    _policy.AllowWritePath(path);
                }
            }
        }
    }

    public CapabilityDecision Evaluate(CapabilityRequest request)
    {
        return _policy.Evaluate(request);
    }

    private static bool HasFileCapability(IReadOnlyList<string>? fileSystem, string capability)
    {
        if (fileSystem is null)
        {
            return false;
        }

        return fileSystem.Any(item =>
            string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Manifest-derived permissions used by the policy evaluator.
/// </summary>
public sealed record ManifestPolicyDefinition(
    bool CacheAllowed,
    IReadOnlyList<string>? Domains,
    IReadOnlyList<string>? Paths,
    IReadOnlyList<string>? FileSystem);
