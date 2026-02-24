namespace EMMA.Api.Configuration;

public sealed class ApiRateLimitOptions
{
    public int PerClientPermitLimit { get; init; } = 60;
    public int PerClientWindowSeconds { get; init; } = 60;
    public int PerClientQueueLimit { get; init; } = 0;
    public int GlobalConcurrencyLimit { get; init; } = 64;
    public int GlobalQueueLimit { get; init; } = 0;
}
