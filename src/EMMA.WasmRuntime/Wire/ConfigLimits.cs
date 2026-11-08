namespace EMMA.WasmRuntime.Wire;

/// <summary>The limits section of the host's plugin configuration.</summary>
/// <param name="CpuBudgetMs">CPU budget per call.</param>
/// <param name="MemoryMb">Memory ceiling.</param>
/// <param name="CallTimeoutMs">Wall-clock limit per call.</param>
internal sealed record ConfigLimits(int CpuBudgetMs, int MemoryMb, int CallTimeoutMs);
