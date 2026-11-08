namespace EMMA.WasmRuntime;

/// <summary>The resource ceilings the host enforces on one plugin.</summary>
/// <param name="CpuBudgetMs">CPU time per call in milliseconds; time spent waiting inside host calls does not count.</param>
/// <param name="MemoryMb">The memory ceiling in megabytes.</param>
/// <param name="CallTimeoutMs">The wall-clock limit for a whole call, host waits included.</param>
public sealed record WasmLimits(int CpuBudgetMs, int MemoryMb, int CallTimeoutMs = 60_000);
