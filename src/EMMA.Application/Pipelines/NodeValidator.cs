using System.Text;
using System.Text.RegularExpressions;

using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Checks what a plugin returned against the contract and turns raw plugin nodes into
/// host nodes. A plugin that breaks the contract produces a failed outcome naming the
/// problem; it never reaches storage or clients.
/// </summary>
internal static partial class NodeValidator
{
    /// <summary>Validates a page of nodes.</summary>
    /// <param name="plugin">The plugin that returned the page.</param>
    /// <param name="info">The plugin's declaration, used to check kinds.</param>
    /// <param name="contextParent">The parent the page was requested for, used when a node omits its parent.</param>
    /// <param name="raw">The raw page.</param>
    /// <returns>The validated page, or the first contract violation.</returns>
    public static Outcome<NodePage> ValidatePage(
        PluginId plugin,
        PluginInfo info,
        LocalNodeId? contextParent,
        PluginNodePage raw)
    {
        if (raw.Items.Count > ContractLimits.MaxNodesPerPage)
        {
            return Violation<NodePage>(plugin, $"returned {raw.Items.Count} nodes in one page (limit {ContractLimits.MaxNodesPerPage})");
        }

        if (raw.NextCursor is not null
            && (raw.NextCursor.Length == 0 || Encoding.UTF8.GetByteCount(raw.NextCursor) > ContractLimits.MaxCursorBytes))
        {
            return Violation<NodePage>(plugin, "returned an empty or oversized cursor");
        }

        Outcome<IReadOnlyList<Node>> nodes = ValidateNodes(plugin, info, contextParent, raw.Items);
        return nodes.IsSuccess
            ? Outcome.Success(new NodePage(nodes.Value, raw.NextCursor, raw.Total, raw.Facets))
            : Outcome.Failure<NodePage>(nodes.Error);
    }

    /// <summary>Validates a list of nodes.</summary>
    /// <param name="plugin">The plugin that returned the nodes.</param>
    /// <param name="info">The plugin's declaration, used to check kinds.</param>
    /// <param name="contextParent">The parent the nodes were requested for, used when a node omits its parent.</param>
    /// <param name="items">The raw nodes.</param>
    /// <returns>The validated nodes in the same order, or the first contract violation.</returns>
    public static Outcome<IReadOnlyList<Node>> ValidateNodes(
        PluginId plugin,
        PluginInfo info,
        LocalNodeId? contextParent,
        IReadOnlyList<PluginNode> items)
    {
        List<Node> nodes = new(items.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (PluginNode item in items)
        {
            Outcome<Node> node = ValidateNode(plugin, info, contextParent, item);
            if (!node.IsSuccess)
            {
                return Outcome.Failure<IReadOnlyList<Node>>(node.Error);
            }

            if (!seen.Add(node.Value.Ref.Id.Value))
            {
                return Violation<IReadOnlyList<Node>>(plugin, $"returned node '{node.Value.Ref.Id}' twice in one response");
            }

            nodes.Add(node.Value);
        }

        return Outcome.Success<IReadOnlyList<Node>>(nodes);
    }

    /// <summary>Validates the props of a node or asset.</summary>
    /// <param name="plugin">The plugin that returned them.</param>
    /// <param name="subject">What the props belong to, for the message (for example <c>node 'x'</c>).</param>
    /// <param name="props">The props.</param>
    /// <returns>The same props, or the first contract violation.</returns>
    public static Outcome<IReadOnlyList<Prop>> ValidateProps(PluginId plugin, string subject, IReadOnlyList<Prop> props)
    {
        if (props.Count > ContractLimits.MaxPropsPerNode)
        {
            return Violation<IReadOnlyList<Prop>>(plugin, $"returned {subject} with {props.Count} props (limit {ContractLimits.MaxPropsPerNode})");
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string key in props.Select(prop => prop.Key))
        {
            if (key.Length > ContractLimits.MaxPropKeyLength || !KeyPattern().IsMatch(key))
            {
                return Violation<IReadOnlyList<Prop>>(plugin, $"returned {subject} with the malformed prop key '{key}'");
            }

            if (!keys.Add(key))
            {
                return Violation<IReadOnlyList<Prop>>(plugin, $"returned {subject} with the prop '{key}' twice");
            }
        }

        return Outcome.Success(props);
    }

    private static Outcome<Node> ValidateNode(
        PluginId plugin,
        PluginInfo info,
        LocalNodeId? contextParent,
        PluginNode raw)
    {
        if (!LocalNodeId.TryCreate(raw.Id, out LocalNodeId id))
        {
            return Violation<Node>(plugin, "returned a node with an empty or oversized id");
        }

        if (!NodeKind.TryCreate(raw.Kind, out NodeKind kind))
        {
            return Violation<Node>(plugin, $"returned node '{id}' with the malformed kind '{raw.Kind}'");
        }

        if (info.FindKind(kind) is null)
        {
            return Violation<Node>(plugin, $"returned node '{id}' of kind '{kind}', which it never declared");
        }

        if (string.IsNullOrWhiteSpace(raw.Title) || Encoding.UTF8.GetByteCount(raw.Title) > ContractLimits.MaxTitleBytes)
        {
            return Violation<Node>(plugin, $"returned node '{id}' with a blank or oversized title");
        }

        LocalNodeId? parentId = contextParent;
        if (raw.Parent is not null)
        {
            if (!LocalNodeId.TryCreate(raw.Parent, out LocalNodeId declaredParent))
            {
                return Violation<Node>(plugin, $"returned node '{id}' with an invalid parent id");
            }

            parentId = declaredParent;
        }

        Outcome<IReadOnlyList<Prop>> props = ValidateProps(plugin, $"node '{id}'", raw.Props);
        if (!props.IsSuccess)
        {
            return Outcome.Failure<Node>(props.Error);
        }

        // A bad thumbnail must not discard an otherwise good page, so it is dropped, not rejected.
        Uri? thumbnail = Uri.TryCreate(raw.Thumbnail, UriKind.Absolute, out Uri? parsed) && IsWebUrl(parsed) ? parsed : null;

        return Outcome.Success(new Node(
            new NodeRef(plugin, id),
            kind,
            raw.Title,
            parentId is { } parent ? new NodeRef(plugin, parent) : null,
            thumbnail,
            props.Value));
    }

    private static bool IsWebUrl(Uri url) =>
        string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        || string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

    private static Outcome<T> Violation<T>(PluginId plugin, string problem) =>
        Outcome.Failure<T>(ErrorCode.Internal, $"Plugin {plugin} {problem}.");

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyPattern();
}
