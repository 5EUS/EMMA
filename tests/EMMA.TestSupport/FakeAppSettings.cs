using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IAppSettingsPort"/>.</summary>
public sealed class FakeAppSettings : IAppSettingsPort
{
    private readonly Dictionary<(UserId User, string Scope), Dictionary<string, string>> values = [];

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, string>> GetAsync(UserId user, string scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(this.values.GetValueOrDefault((user, scope)) ?? [], StringComparer.Ordinal));

    /// <inheritdoc/>
    public Task ApplyAsync(UserId user, string scope, IReadOnlyList<AppSettingChange> changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (!this.values.TryGetValue((user, scope), out Dictionary<string, string>? current))
        {
            this.values[(user, scope)] = current = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (AppSettingChange change in changes)
        {
            if (change.Value is null)
            {
                current.Remove(change.Key);
            }
            else
            {
                current[change.Key] = change.Value;
            }
        }

        return Task.CompletedTask;
    }
}
