using EMMA.Domain;

namespace EMMA.Tests.Domain;

public class CapabilityPolicyTests
{
    [Fact]
    public void Evaluate_Denies_ByDefault()
    {
        var policy = new CapabilityPolicy();

        var network = policy.Evaluate(new CapabilityRequest(CapabilityKind.Network, "example.com"));
        var read = policy.Evaluate(new CapabilityRequest(CapabilityKind.FileRead, "/data"));
        var write = policy.Evaluate(new CapabilityRequest(CapabilityKind.FileWrite, "/data"));
        var cache = policy.Evaluate(new CapabilityRequest(CapabilityKind.Cache, null));

        Assert.False(network.Allowed);
        Assert.False(read.Allowed);
        Assert.False(write.Allowed);
        Assert.False(cache.Allowed);
    }

    [Fact]
    public void Evaluate_Allows_Configured_Network_Domain()
    {
        var policy = new CapabilityPolicy();
        policy.AllowNetworkDomain("example.com");

        var decision = policy.Evaluate(new CapabilityRequest(CapabilityKind.Network, "example.com"));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_Allows_Configured_Paths()
    {
        var policy = new CapabilityPolicy();
        policy.AllowReadPath("/data");
        policy.AllowWritePath("/data");

        var read = policy.Evaluate(new CapabilityRequest(CapabilityKind.FileRead, "/data/files"));
        var write = policy.Evaluate(new CapabilityRequest(CapabilityKind.FileWrite, "/data/output"));

        Assert.True(read.Allowed);
        Assert.True(write.Allowed);
    }

    [Fact]
    public void Evaluate_Allows_Cache_WhenEnabled()
    {
        var policy = new CapabilityPolicy();
        policy.AllowCache(true);

        var decision = policy.Evaluate(new CapabilityRequest(CapabilityKind.Cache, null));

        Assert.True(decision.Allowed);
    }
}
