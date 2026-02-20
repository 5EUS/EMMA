using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Port for capability checks before performing guarded operations.
/// </summary>
public interface IPolicyEvaluator
{
    /// <summary>
    /// Evaluates a capability request and returns an allow/deny decision.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    CapabilityDecision Evaluate(CapabilityRequest request);
}
