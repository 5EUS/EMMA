using System.Globalization;

using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// What a value of a declared setting may be (contract section 12). The same rules check a plugin's own defaults
/// when it declares itself and a user's value when they change a setting, so a plugin cannot declare a default
/// the user could not have chosen.
/// </summary>
internal static class SettingValues
{
    /// <summary>Checks one value as text, for the setting's type.</summary>
    /// <param name="setting">The declaration.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value is acceptable for a setting of that type.</returns>
    public static bool IsValid(SettingSpec setting, string value) => setting.Type switch
    {
        SettingType.Boolean => value is "true" or "false",
        SettingType.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number),
        SettingType.Duration => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
        SettingType.Choice or SettingType.MultiChoice => setting.Choices.Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal)),
        SettingType.Text or SettingType.Secret => setting.MaxLength is not { } max || value.Length <= max,
        _ => false,
    };

    /// <summary>Finds what is wrong with the values a user wants to set, if anything.</summary>
    /// <param name="setting">The declaration.</param>
    /// <param name="values">The proposed values. None means clearing the setting, which is always allowed.</param>
    /// <returns>A sentence a person can act on, or <see langword="null"/> when the values are acceptable.</returns>
    public static string? Problem(SettingSpec setting, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (setting.Type != SettingType.MultiChoice && values.Count > 1)
        {
            return $"'{setting.Key}' takes a single value.";
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            return $"'{setting.Key}' lists a value twice.";
        }

        foreach (string value in values)
        {
            string? problem = ValueProblem(setting, value);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? ValueProblem(SettingSpec setting, string value)
    {
        if (value.Length > ContractLimits.MaxSettingValueLength)
        {
            return $"A value of '{setting.Key}' is longer than {ContractLimits.MaxSettingValueLength} characters.";
        }

        if (setting.Type == SettingType.Secret && value.Length == 0)
        {
            return $"'{setting.Key}' is a secret and cannot be empty; clear it instead.";
        }

        return IsValid(setting, value) ? null : $"'{value}' is not a valid {Describe(setting)} for '{setting.Key}'.";
    }

    private static string Describe(SettingSpec setting) => setting.Type switch
    {
        SettingType.Boolean => "boolean (true or false)",
        SettingType.Number => "number",
        SettingType.Duration => "duration (whole milliseconds)",
        SettingType.Choice or SettingType.MultiChoice => "choice",
        _ => $"text of at most {setting.MaxLength} characters",
    };
}
