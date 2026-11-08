using System.Text.RegularExpressions;

using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Checks what a plugin declares in <c>meta.describe</c> against the contract, once, whichever
/// transport produced it: kinds that refer to kinds that do not exist, settings whose defaults or
/// visibility rules cannot work, secrets with defaults. A plugin that declares nonsense is refused
/// before any client builds a screen from it.
/// </summary>
internal static partial class PluginInfoValidator
{
    private const int MaxKinds = 256;
    private const int MaxFilters = 256;
    private const int MaxSettings = 256;
    private const int MaxCategories = 64;

    /// <summary>Finds the first contract violation in a plugin's declaration.</summary>
    /// <param name="info">The declaration.</param>
    /// <returns>A description of the first problem, or <see langword="null"/> when the declaration is valid.</returns>
    public static string? FindProblem(PluginInfo info)
    {
        if (info.Kinds.Count > MaxKinds || info.Filters.Count > MaxFilters || info.Settings.Count > MaxSettings || info.Categories.Count > MaxCategories)
        {
            return "declared more kinds, filters, settings or categories than the contract allows";
        }

        return FindKindProblem(info)
            ?? FindUniquenessProblem(info)
            ?? FindSettingsProblem(info);
    }

    private static string? FindKindProblem(PluginInfo info)
    {
        HashSet<NodeKind> declared = [.. info.Kinds.Select(kind => kind.Kind)];
        if (declared.Count != info.Kinds.Count)
        {
            return "declared the same kind twice";
        }

        foreach (KindInfo kind in info.Kinds)
        {
            NodeKind? unknown = kind.Children.Where(child => !declared.Contains(child)).Select(child => (NodeKind?)child).FirstOrDefault();
            if (unknown is { } missing)
            {
                return $"declared kind '{kind.Kind}' with child kind '{missing}', which it never declared";
            }
        }

        string? badFilterKind = info.Filters
            .SelectMany(filter => filter.Kinds)
            .Where(kind => !declared.Contains(kind))
            .Select(kind => kind.Value)
            .FirstOrDefault();
        return badFilterKind is null ? null : $"declared a filter for kind '{badFilterKind}', which it never declared";
    }

    private static string? FindUniquenessProblem(PluginInfo info)
    {
        if (info.Filters.Select(filter => filter.Id).Distinct(StringComparer.Ordinal).Count() != info.Filters.Count)
        {
            return "declared the same filter id twice";
        }

        if (info.Sorts.Select(sort => sort.Value).Distinct(StringComparer.Ordinal).Count() != info.Sorts.Count)
        {
            return "declared the same sort twice";
        }

        if (info.Categories.Select(category => category.Id).Distinct(StringComparer.Ordinal).Count() != info.Categories.Count)
        {
            return "declared the same settings category twice";
        }

        return info.Settings.Select(setting => setting.Key).Distinct(StringComparer.Ordinal).Count() != info.Settings.Count
            ? "declared the same setting key twice"
            : null;
    }

    private static string? FindSettingsProblem(PluginInfo info)
    {
        HashSet<string> categories = [.. info.Categories.Select(category => category.Id)];
        Dictionary<string, SettingSpec> byKey = info.Settings.ToDictionary(setting => setting.Key, StringComparer.Ordinal);

        foreach (SettingSpec setting in info.Settings)
        {
            string? problem = FindSettingProblem(setting, categories, byKey);
            if (problem is not null)
            {
                return $"declared setting '{setting.Key}' wrongly: {problem}";
            }
        }

        return FindVisibilityCycle(info.Settings);
    }

    private static string? FindSettingProblem(SettingSpec setting, HashSet<string> categories, Dictionary<string, SettingSpec> byKey)
    {
        bool isChoice = setting.Type is SettingType.Choice or SettingType.MultiChoice;
        if (!KeyPattern().IsMatch(setting.Key))
        {
            return "the key must be letters, digits, '.', '_' or '-', starting with a letter";
        }

        if (setting.Category is not null && !categories.Contains(setting.Category))
        {
            return $"it names the category '{setting.Category}', which is not declared";
        }

        if (isChoice != (setting.Choices.Count > 0))
        {
            return isChoice ? "a choice setting needs choices" : "only choice settings may have choices";
        }

        if (setting.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal).Count() != setting.Choices.Count)
        {
            return "two choices have the same value";
        }

        if (setting.MaxLength is not null && setting.Type is not (SettingType.Text or SettingType.Secret))
        {
            return "only text and secret settings have a maximum length";
        }

        return FindDefaultProblem(setting) ?? FindConditionProblem(setting, byKey);
    }

    private static string? FindDefaultProblem(SettingSpec setting)
    {
        if (setting.Type == SettingType.Secret && setting.DefaultValues.Count > 0)
        {
            return "a secret must not have a default";
        }

        if (setting.DefaultValues.Count > 1 && setting.Type != SettingType.MultiChoice)
        {
            return "only a multi-choice setting may have several defaults";
        }

        foreach (string value in setting.DefaultValues)
        {
            if (!SettingValues.IsValid(setting, value))
            {
                return $"the default '{value}' is not a valid {setting.Type} value";
            }
        }

        return setting.DefaultValues.Distinct(StringComparer.Ordinal).Count() != setting.DefaultValues.Count ? "a default is listed twice" : null;
    }

    private static string? FindConditionProblem(SettingSpec setting, Dictionary<string, SettingSpec> byKey)
    {
        if (setting.VisibleWhen is not { } condition)
        {
            return null;
        }

        if (string.Equals(condition.Key, setting.Key, StringComparison.Ordinal))
        {
            return "it can only be shown depending on another setting";
        }

        if (!byKey.TryGetValue(condition.Key, out SettingSpec? other))
        {
            return $"it is shown depending on '{condition.Key}', which is not declared";
        }

        return SettingValues.IsValid(other, condition.Value)
            ? null
            : $"it is shown when '{condition.Key}' is '{condition.Value}', which that setting cannot have";
    }

    private static string? FindVisibilityCycle(IReadOnlyList<SettingSpec> settings)
    {
        Dictionary<string, string> dependsOn = settings
            .Where(setting => setting.VisibleWhen is not null)
            .ToDictionary(setting => setting.Key, setting => setting.VisibleWhen!.Key, StringComparer.Ordinal);

        foreach (string start in dependsOn.Keys)
        {
            HashSet<string> seen = new(StringComparer.Ordinal) { start };
            string current = start;
            while (dependsOn.TryGetValue(current, out string? next))
            {
                if (!seen.Add(next))
                {
                    return $"declared settings whose visibility rules depend on each other in a cycle (at '{next}')";
                }

                current = next;
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyPattern();
}
