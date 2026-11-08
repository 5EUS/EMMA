using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps installed plugins and their declarations to their wire form.</summary>
public static class PluginMapper
{
    /// <summary>Maps an installed plugin to its list entry.</summary>
    /// <param name="plugin">The record.</param>
    /// <returns>The wire form.</returns>
    public static PluginSummary ToSummary(InstalledPlugin plugin) =>
        new(plugin.Id.Value, plugin.Version, SnakeCase.Of(plugin.Status), plugin.Signer);

    /// <summary>Maps an installed plugin with its declaration.</summary>
    /// <param name="plugin">The record.</param>
    /// <param name="info">The declaration, when the plugin answered.</param>
    /// <returns>The wire form.</returns>
    public static PluginDetailResponse ToDetail(InstalledPlugin plugin, PluginInfo? info) =>
        new(plugin.Id.Value, plugin.Version, SnakeCase.Of(plugin.Status), plugin.Signer, info is null ? null : ToDto(info));

    private static PluginInfoDto ToDto(PluginInfo info) => new(
        [.. info.Kinds.Select(kind => new KindDto(
            kind.Kind.Value,
            kind.Label,
            [.. kind.Children.Select(child => child.Value)],
            [.. kind.Roles.Select(role => role.Value)],
            kind.Profile))],
        [.. info.Filters.Select(ToDto)],
        [.. info.Sorts.Select(ToDto)],
        [.. info.Categories.Select(category => new CategoryDto(category.Id, category.Label, category.Description))],
        [.. info.Settings.Select(ToDto)]);

    private static ChoiceDto ToDto(Choice choice) => new(choice.Value, choice.Label, choice.Description, choice.IsDefault);

    private static FilterDto ToDto(FilterSpec filter) => new(
        filter.Id,
        filter.Label,
        SnakeCase.Of(filter.Type),
        [.. filter.Kinds.Select(kind => kind.Value)],
        filter.Required,
        filter.Hidden,
        filter.Group,
        filter.Description,
        [.. filter.Choices.Select(ToDto)],
        filter.Range is { } range ? new RangeDto(range.Min, range.Max, range.Step, range.Unit) : null,
        filter.Lookup is { } lookup ? new LookupDto(lookup.MinQueryLength, lookup.MaxResults, lookup.DebounceMs, lookup.AllowCustomValues) : null);

    private static SettingDto ToDto(SettingSpec setting) => new(
        setting.Key,
        setting.Label,
        setting.Description,
        setting.Help,
        SnakeCase.Of(setting.Type),
        setting.Category,
        setting.Required,
        setting.DefaultValues,
        [.. setting.Choices.Select(ToDto)],
        setting.Placeholder,
        setting.MaxLength,
        setting.VisibleWhen is { } condition ? new ConditionDto(condition.Key, condition.Value) : null);
}
