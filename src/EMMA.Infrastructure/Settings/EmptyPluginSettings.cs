using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Settings;

/// <summary>
/// Reports that no user has set anything, so every plugin runs on its declared defaults. It stands in until
/// a settings store exists (see the note in EMMA.Infrastructure.csproj).
/// </summary>
public sealed class EmptyPluginSettings : IPluginSettingsPort
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken) =>
        Task.FromResult(None);
}
