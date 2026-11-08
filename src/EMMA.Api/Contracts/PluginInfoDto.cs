namespace EMMA.Api.Contracts;

/// <summary>What a plugin declares about itself.</summary>
/// <param name="Kinds">Every kind it produces.</param>
/// <param name="Filters">Search filters.</param>
/// <param name="Sorts">Sort orders.</param>
/// <param name="Categories">Settings groups.</param>
/// <param name="Settings">User settings.</param>
internal sealed record PluginInfoDto(
    IReadOnlyList<KindDto> Kinds,
    IReadOnlyList<FilterDto> Filters,
    IReadOnlyList<ChoiceDto> Sorts,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<SettingDto> Settings);
