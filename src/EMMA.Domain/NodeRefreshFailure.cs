namespace EMMA.Domain;

/// <summary>A saved node that could not be refreshed, and why.</summary>
/// <param name="Node">The saved node.</param>
/// <param name="Error">What went wrong, as the plugin or the host reported it.</param>
public sealed record NodeRefreshFailure(NodeRef Node, PluginError Error);
