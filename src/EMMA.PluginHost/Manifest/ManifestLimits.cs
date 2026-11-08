namespace EMMA.PluginHost.Manifest;

/// <summary>Resource ceilings the Wasm runtime enforces.</summary>
/// <param name="CpuBudgetMs">The CPU budget per call, in milliseconds.</param>
/// <param name="MemoryMb">The memory ceiling in megabytes.</param>
public sealed record ManifestLimits(int CpuBudgetMs, int MemoryMb);
