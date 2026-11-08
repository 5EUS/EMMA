namespace EMMA.Domain;

/// <summary>What a running plugin says about itself through <c>meta.describe</c>.</summary>
/// <param name="Kinds">Every kind the plugin can produce.</param>
/// <param name="Filters">Declared search filters.</param>
/// <param name="Sorts">Available sort orders.</param>
/// <param name="Settings">Declared user settings.</param>
public sealed record PluginInfo(
    IReadOnlyList<KindInfo> Kinds,
    IReadOnlyList<FilterSpec> Filters,
    IReadOnlyList<Choice> Sorts,
    IReadOnlyList<SettingSpec> Settings)
{
    /// <summary>Finds the declaration of a kind.</summary>
    /// <param name="kind">The kind to look up.</param>
    /// <returns>The declaration, or <see langword="null"/> when the plugin never declared it.</returns>
    public KindInfo? FindKind(NodeKind kind) => this.Kinds.FirstOrDefault(info => info.Kind == kind);
}
