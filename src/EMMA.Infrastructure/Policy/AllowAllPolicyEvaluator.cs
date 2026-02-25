using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Policy;

/// <summary>
/// Policy evaluator that allows all capability requests.
/// </summary>
[Obsolete("AllowAllPolicyEvaluator is deprecated. Replace with a real policy evaluator before production use.")]
public sealed class AllowAllPolicyEvaluator : IPolicyEvaluator
{
    /// <summary>
    /// Always returns an allow decision.
    /// </summary>
    public CapabilityDecision Evaluate(CapabilityRequest request)
    {
        return new CapabilityDecision(true, null);
    }
}
