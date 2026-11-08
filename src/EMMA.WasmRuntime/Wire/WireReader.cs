using EMMA.Domain;

namespace EMMA.WasmRuntime.Wire;

/// <summary>Turns what the host returned into Domain types. Anything off-contract throws <see cref="WireFormatException"/>.</summary>
internal static class WireReader
{
    /// <summary>Maps a plugin's self-description.</summary>
    /// <param name="info">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static PluginInfo Info(WireInfo info) => new(
        [.. info.Kinds.Select(Kind)],
        [.. info.Filters.Select(Filter)],
        [.. info.Sorts.Select(Choice)],
        [.. info.Categories.Select(category => new SettingCategory(category.Id, category.Label, category.Description))],
        [.. info.Settings.Select(Setting)]);

    /// <summary>Maps a page of nodes.</summary>
    /// <param name="page">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static PluginNodePage Page(WireNodePage page) => new(
        [.. page.Items.Select(Node)],
        page.NextCursor,
        page.Total is { } total ? ToLong(total) : null,
        [.. page.Facets.Select(facet => new Facet(facet.FilterId, [.. facet.Counts.Select(count => new FacetCount(count.Value, count.Label, ToLong(count.Count)))]))]);

    /// <summary>Maps a node.</summary>
    /// <param name="node">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static PluginNode Node(WireNode node) =>
        new(node.Id, node.Kind, node.Title, node.Parent, node.Thumbnail, [.. node.Props.Select(Prop)]);

    /// <summary>Maps an asset.</summary>
    /// <param name="asset">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static Asset Asset(WireAsset asset) =>
        new(asset.Mime, Delivery(asset.Delivery), asset.Size is { } size ? ToLong(size) : null, [.. asset.Props.Select(Prop)]);

    /// <summary>Maps a failure.</summary>
    /// <param name="error">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static PluginError Error(WireError error) =>
        new(error.Code, error.Message, error.RetryAfterMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    /// <summary>Maps a property.</summary>
    /// <param name="prop">The wire form.</param>
    /// <returns>The Domain form.</returns>
    internal static Prop Prop(WireProp prop) => new(prop.Key, PropValue(prop.Value));

    private static long ToLong(ulong value) =>
        value <= long.MaxValue ? (long)value : throw new WireFormatException("A count or size is larger than 2^63.");

    private static PropValue PropValue(WirePropValue value)
    {
        PropValue?[] candidates =
        [
            value.Text is null ? null : Domain.PropValue.FromText(value.Text),
            value.Integer is null ? null : Domain.PropValue.FromWholeNumber(value.Integer.Value),
            value.Number is null ? null : Domain.PropValue.FromNumber(value.Number.Value),
            value.Flag is null ? null : Domain.PropValue.FromFlag(value.Flag.Value),
            value.TimestampMs is null ? null : Domain.PropValue.FromTimestamp(value.TimestampMs.Value),
            value.Url is null ? null : Domain.PropValue.FromUrl(AbsoluteUri(value.Url)),
            value.TextList is null ? null : Domain.PropValue.FromTextList(value.TextList),
        ];
        PropValue[] present = [.. candidates.OfType<PropValue>()];
        return present.Length == 1 ? present[0] : throw new WireFormatException("A property value must carry exactly one variant.");
    }

    private static Uri AbsoluteUri(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? uri : throw new WireFormatException($"'{text}' is not an absolute URL.");

    private static AssetDelivery Delivery(WireDelivery delivery)
    {
        int set = (delivery.Inline is null ? 0 : 1) + (delivery.Remote is null ? 0 : 1) + (delivery.Proxied is null ? 0 : 1);
        if (set != 1)
        {
            throw new WireFormatException("An asset delivery must carry exactly one variant.");
        }

        if (delivery.Inline is not null)
        {
            return new InlineDelivery(Convert.FromBase64String(delivery.Inline.Base64));
        }

        if (delivery.Remote is not null)
        {
            return new RemoteDelivery(
                AbsoluteUri(delivery.Remote.Url),
                [.. delivery.Remote.Headers.Select(header => new HttpHeader(header.Name, header.Value))],
                delivery.Remote.ExpiresMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
        }

        return new ProxiedDelivery(delivery.Proxied!.Token);
    }

    private static NodeKind KindName(string value) =>
        NodeKind.TryCreate(value, out NodeKind kind) ? kind : throw new WireFormatException($"'{value}' is not a valid node kind.");

    private static AssetRole Role(string value) =>
        AssetRole.TryCreate(value, out AssetRole role) ? role : throw new WireFormatException($"'{value}' is not a valid asset role.");

    private static KindInfo Kind(WireKind kind) => new(
        KindName(kind.Kind),
        kind.Label,
        [.. kind.Children.Select(KindName)],
        [.. kind.Roles.Select(Role)],
        kind.Profile);

    private static Choice Choice(WireChoice choice) => new(choice.Value, choice.Label, choice.Description, choice.IsDefault);

    private static FilterSpec Filter(WireFilter filter) => new(
        filter.Id,
        filter.Label,
        filter.FilterType,
        [.. filter.Kinds.Select(KindName)],
        filter.Required,
        filter.Hidden,
        filter.Group,
        filter.Description,
        [.. filter.Choices.Select(Choice)],
        filter.Range is { } range ? new RangeSpec(range.Min, range.Max, range.Step, range.Unit) : null,
        filter.Lookup is { } lookup ? Lookup(lookup) : null);

    private static LookupSpec Lookup(WireLookup lookup) =>
        new(ToInt(lookup.MinQueryLength), ToInt(lookup.MaxResults), ToInt(lookup.DebounceMs), lookup.AllowCustomValues);

    private static SettingSpec Setting(WireSetting setting) => new(
        setting.Key,
        setting.Label,
        setting.Description,
        setting.Help,
        setting.SettingType,
        setting.Category,
        setting.Required,
        setting.DefaultValues,
        [.. setting.Choices.Select(Choice)],
        setting.Placeholder,
        setting.MaxLength is { } max ? ToInt(max) : null,
        setting.VisibleWhen is { } when ? new SettingCondition(when.Key, when.EqualsValue) : null);

    private static int ToInt(uint value) =>
        value <= int.MaxValue ? (int)value : throw new WireFormatException("A declared number is too large.");
}
