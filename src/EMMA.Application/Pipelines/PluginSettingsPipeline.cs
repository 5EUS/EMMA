using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Reads and changes the user's values for a plugin's declared settings. What may be set comes from the plugin's own
/// declaration (<c>meta.describe</c>), so the host knows no setting by name, and every change is checked against it
/// before anything is stored. A plugin only sees new values when it is opened again; use
/// <c>EmmaRuntime.SetPluginSettingsAsync</c>, which closes it afterwards.
/// </summary>
/// <param name="gateway">Asks the plugin what it declares.</param>
/// <param name="store">Where the values are kept.</param>
public sealed class PluginSettingsPipeline(IPluginGateway gateway, IPluginSettingsPort store)
{
    /// <summary>Gets where each declared setting stands.</summary>
    /// <param name="plugin">The plugin, which must be enabled so it can say what it declares.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One state per declared setting, in declaration order, or why the plugin could not be asked.</returns>
    public async Task<Outcome<IReadOnlyList<SettingState>>> GetAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await gateway.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Outcome.Success(await this.StatesAsync(plugin, info.Value, cancellationToken).ConfigureAwait(false))
            : Outcome.Failure<IReadOnlyList<SettingState>>(info.Error);
    }

    /// <summary>Changes some of a plugin's settings, all or none.</summary>
    /// <param name="plugin">The plugin, which must be enabled so it can say what it declares.</param>
    /// <param name="changes">The settings to change, each at most once. A setting left out keeps its value; a change with no values clears it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings as they stand after the change, or why nothing was changed.</returns>
    public async Task<Outcome<IReadOnlyList<SettingState>>> SetAsync(
        PluginId plugin,
        IReadOnlyList<SettingChange> changes,
        CancellationToken cancellationToken)
    {
        Outcome<PluginInfo> info = await gateway.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<SettingState>>(info.Error);
        }

        Outcome<IReadOnlyList<SettingWrite>> writes = Validate(info.Value, changes);
        if (!writes.IsSuccess)
        {
            return Outcome.Failure<IReadOnlyList<SettingState>>(writes.Error);
        }

        if (writes.Value.Count > 0)
        {
            await store.ApplyAsync(plugin, writes.Value, cancellationToken).ConfigureAwait(false);
        }

        return Outcome.Success(await this.StatesAsync(plugin, info.Value, cancellationToken).ConfigureAwait(false));
    }

    private static Outcome<IReadOnlyList<SettingWrite>> Validate(PluginInfo info, IReadOnlyList<SettingChange> changes)
    {
        Dictionary<string, SettingSpec> declared = info.Settings.ToDictionary(setting => setting.Key, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<SettingWrite> writes = [];
        foreach (SettingChange change in changes)
        {
            if (!declared.TryGetValue(change.Key, out SettingSpec? spec))
            {
                return Invalid($"The plugin declares no setting '{change.Key}'.");
            }

            if (!seen.Add(change.Key))
            {
                return Invalid($"'{change.Key}' is changed twice in one request.");
            }

            string? problem = SettingValues.Problem(spec, change.Values);
            if (problem is not null)
            {
                return Invalid(problem);
            }

            writes.Add(new SettingWrite(change.Key, change.Values, spec.Type == SettingType.Secret));
        }

        return Outcome.Success<IReadOnlyList<SettingWrite>>(writes);
    }

    private static Outcome<IReadOnlyList<SettingWrite>> Invalid(string message) =>
        Outcome.Failure<IReadOnlyList<SettingWrite>>(ErrorCode.InvalidArgument, message);

    private async Task<IReadOnlyList<SettingState>> StatesAsync(PluginId plugin, PluginInfo info, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredSetting> stored = await store.ListAsync(plugin, cancellationToken).ConfigureAwait(false);
        Dictionary<string, StoredSetting> byKey = stored.ToDictionary(setting => setting.Key, StringComparer.Ordinal);
        return
        [
            .. info.Settings.Select(spec =>
            {
                bool isSet = byKey.TryGetValue(spec.Key, out StoredSetting? value);
                return new SettingState(
                    spec.Key,
                    isSet,
                    isSet ? value!.Values : [],
                    Missing: spec.Required && !isSet && spec.DefaultValues.Count == 0);
            }),
        ];
    }
}
