using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Policy;

/// <summary>
/// Host-side policy evaluator for trusted runtime services.
/// </summary>
public sealed class HostPolicyEvaluator : IPolicyEvaluator
{
    private static readonly string[] NetworkTargets =
    [
        "search",
        "chapters",
        "page",
        "page-asset"
    ];

    private readonly CapabilityPolicy _policy = new();

    public HostPolicyEvaluator(bool allowCache = true, bool allowNetwork = true)
    {
        _policy.AllowCache(allowCache);

        if (allowNetwork)
        {
            foreach (var target in NetworkTargets)
            {
                _policy.AllowNetworkDomain(target);
            }
        }
    }

    public CapabilityDecision Evaluate(CapabilityRequest request)
    {
        return _policy.Evaluate(request);
    }
}
