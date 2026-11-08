namespace EMMA.Api.Configuration;

/// <summary>Everything the host needs to run, read once and validated at startup.</summary>
/// <param name="DataDirectory">The directory EMMA keeps its state in.</param>
/// <param name="Trust">Which plugin signatures are accepted.</param>
/// <param name="Security">Authentication and rate limits.</param>
internal sealed record ApiSettings(string DataDirectory, TrustSettings Trust, SecuritySettings Security);
