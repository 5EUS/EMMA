using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps plugin settings to and from their wire form.</summary>
public static class SettingsMapper
{
    /// <summary>Maps where each setting stands to its wire form.</summary>
    /// <param name="states">The settings.</param>
    /// <returns>The wire form.</returns>
    public static PluginSettingsResponse ToResponse(IReadOnlyList<SettingState> states) =>
        new([.. states.Select(state => new SettingStateDto(state.Key, state.IsSet, state.Values, state.Missing))]);

    /// <summary>Reads the changes a client asked for.</summary>
    /// <param name="changes">The wire form, or <see langword="null"/> when the request had none.</param>
    /// <returns>The changes, or why the request is malformed. Whether a value suits its setting is checked later, against what the plugin declared.</returns>
    public static Outcome<IReadOnlyList<SettingChange>> ToChanges(IReadOnlyList<SettingChangeDto>? changes)
    {
        if (changes is null)
        {
            return Outcome.Failure<IReadOnlyList<SettingChange>>(ErrorCode.InvalidArgument, "'changes' is required.");
        }

        List<SettingChange> parsed = [];
        foreach (SettingChangeDto change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Key) || change.Values is null)
            {
                return Outcome.Failure<IReadOnlyList<SettingChange>>(ErrorCode.InvalidArgument, "Every change needs a 'key' and 'values' (an empty list clears the setting).");
            }

            parsed.Add(new SettingChange(change.Key, change.Values));
        }

        return Outcome.Success<IReadOnlyList<SettingChange>>(parsed);
    }
}
