using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Policy;

/// <summary>
/// Works out the network a plugin may reach once the user's own base URLs are counted. A manifest lists the settings that
/// hold such a URL (<see cref="ManifestPermissions.NetworkSettings"/>); the host of each value joins the signed allow-list
/// for that plugin only. Loopback and private addresses stay refused unless the user turns on the reserved boolean
/// setting <see cref="AllowLocalNetworkKey"/>, and that only counts for a plugin that lists a URL setting at all.
/// </summary>
public static class SettingNetworkHosts
{
    /// <summary>The reserved setting key a plugin declares so the user can permit loopback and private addresses.</summary>
    public const string AllowLocalNetworkKey = "emma.allow-local-network";

    /// <summary>Gets the allow-list a plugin runs with: the manifest's hosts plus the hosts of its URL settings.</summary>
    /// <param name="permissions">The manifest's permissions.</param>
    /// <param name="settings">The user's settings as text values by key.</param>
    /// <returns>Exact host names and wildcards, with no repeats.</returns>
    public static IReadOnlyList<string> Effective(ManifestPermissions permissions, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        if (permissions.NetworkSettings.Count == 0)
        {
            return permissions.Network;
        }

        List<string> hosts = [.. permissions.Network];
        foreach (string key in permissions.NetworkSettings)
        {
            if (settings.TryGetValue(key, out IReadOnlyList<string>? values) && values.Count > 0 && HostOf(values[0]) is { } host && !hosts.Contains(host, StringComparer.Ordinal))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    /// <summary>Decides whether the user let this plugin reach loopback and private addresses.</summary>
    /// <param name="permissions">The manifest's permissions.</param>
    /// <param name="settings">The user's settings as text values by key.</param>
    /// <returns><see langword="true"/> only when the manifest has URL settings and the user switched the toggle on.</returns>
    public static bool AllowsLocalNetwork(ManifestPermissions permissions, IReadOnlyDictionary<string, IReadOnlyList<string>> settings) =>
        permissions.NetworkSettings.Count > 0
        && settings.TryGetValue(AllowLocalNetworkKey, out IReadOnlyList<string>? values)
        && values.Count == 1
        && string.Equals(values[0], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the host out of a base URL setting.</summary>
    /// <param name="value">What the user typed.</param>
    /// <returns>The host (a <see cref="Uri"/> already lower-cases it), or <see langword="null"/> when the value is not an http or https URL with a host and no credentials.</returns>
    public static string? HostOf(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? url)
        && (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        && url.UserInfo.Length == 0
        && url.IdnHost.Length > 0
            ? url.IdnHost
            : null;
}
