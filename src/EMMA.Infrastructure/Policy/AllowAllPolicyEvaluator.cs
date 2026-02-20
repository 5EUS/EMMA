using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Policy;

/// <summary>
/// Policy evaluator that allows all capability requests.
/// </summary>
public sealed class AllowAllPolicyEvaluator : IPolicyEvaluator
{
    // TODO: Deprecate this permissive evaluator once a real policy engine exists.
    /// <summary>
    /// Always returns an allow decision.
    /// </summary>
    public CapabilityDecision Evaluate(CapabilityRequest request)
    {
        return new CapabilityDecision(true, null);
    }
}
