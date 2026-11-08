namespace EMMA.PluginHost.Signing;

/// <summary>Whether a delegated signing key may currently sign plugins.</summary>
public enum DelegationStatus
{
    /// <summary>The key may sign, within its validity window and scopes.</summary>
    Active,

    /// <summary>The key was rotated out and signs nothing new; signatures it made inside its validity window stay valid.</summary>
    Retired,

    /// <summary>The key is compromised or withdrawn; nothing it signed is trusted.</summary>
    Revoked,
}
