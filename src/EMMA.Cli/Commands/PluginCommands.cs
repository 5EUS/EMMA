using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Cli.Commands;

/// <summary>Manages the plugins installed in a data directory. The HTTP API and the native library serve whatever is installed here.</summary>
internal static class PluginCommands
{
    /// <summary>Lists installed plugins.</summary>
    /// <param name="context">The command.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ListAsync(CommandContext context) =>
        WithRuntimeAsync(context, async runtime =>
        {
            IReadOnlyList<InstalledPlugin> plugins = await runtime.Registry.ListAsync(context.CancellationToken).ConfigureAwait(false);
            if (context.Arguments.Json)
            {
                await PrintAsync(context, new PluginListResponse([.. plugins.Select(PluginMapper.ToSummary)]), CliJsonContext.Default.PluginListResponse).ConfigureAwait(false);
                return CommandContext.Success;
            }

            if (plugins.Count == 0)
            {
                await context.SayAsync("No plugins installed.").ConfigureAwait(false);
            }

            foreach (InstalledPlugin plugin in plugins)
            {
                await context.SayAsync($"{plugin.Id,-40} {plugin.Version,-12} {SnakeCase.Of(plugin.Status),-12} {plugin.Signer ?? "unsigned"}").ConfigureAwait(false);
            }

            return CommandContext.Success;
        });

    /// <summary>Shows one plugin and what it declares.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> InfoAsync(CommandContext context, string id) =>
        WithPluginAsync(
            context,
            id,
            async (runtime, plugin) =>
            {
                Outcome<PluginDetail> detail = await runtime.DescribePluginAsync(plugin, context.CancellationToken).ConfigureAwait(false);
                if (!detail.IsSuccess)
                {
                    return await context.FailAsync(detail.Error).ConfigureAwait(false);
                }

                await PrintDetailAsync(context, detail.Value).ConfigureAwait(false);
                return CommandContext.Success;
            });

    /// <summary>Installs a package.</summary>
    /// <param name="context">The command.</param>
    /// <param name="path">The package file.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> InstallAsync(CommandContext context, string path)
    {
        string? repository = context.Arguments.Option("repository");
        if (string.IsNullOrWhiteSpace(repository))
        {
            return context.MisuseAsync("--repository is required: the id of the trusted repository the package came from.");
        }

        if (!File.Exists(path))
        {
            return context.FailAsync($"{path} does not exist.");
        }

        return WithRuntimeAsync(
            context,
            async runtime =>
            {
                FileStream package = File.OpenRead(path);
                await using (package.ConfigureAwait(false))
                {
                    Outcome<InstalledPlugin> installed = await runtime.InstallAsync(package, repository, context.CancellationToken).ConfigureAwait(false);
                    if (!installed.IsSuccess)
                    {
                        return await context.FailAsync(installed.Error).ConfigureAwait(false);
                    }

                    if (context.Arguments.Json)
                    {
                        await PrintAsync(context, PluginMapper.ToSummary(installed.Value), CliJsonContext.Default.PluginSummary).ConfigureAwait(false);
                    }
                    else
                    {
                        await context.SayAsync($"Installed {installed.Value.Id} {installed.Value.Version} ({installed.Value.Signer ?? "unsigned"}).").ConfigureAwait(false);
                    }

                    return CommandContext.Success;
                }
            });
    }

    /// <summary>Enables or disables a plugin.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="status">The new status.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> SetStatusAsync(CommandContext context, string id, PluginStatus status) =>
        ChangeAsync(
            context,
            id,
            (runtime, plugin) => runtime.SetStatusAsync(plugin, status, context.CancellationToken),
            $"{(status == PluginStatus.Enabled ? "Enabled" : "Disabled")} {id}.");

    /// <summary>Removes a plugin and its files.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RemoveAsync(CommandContext context, string id) =>
        ChangeAsync(context, id, (runtime, plugin) => runtime.UninstallAsync(plugin, context.CancellationToken), $"Removed {id}.");

    /// <summary>Writes a value as JSON.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="context">The command.</param>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">How to write it.</param>
    /// <returns>A task that completes when it is written.</returns>
    internal static Task PrintAsync<T>(CommandContext context, T value, JsonTypeInfo<T> typeInfo) =>
        context.SayAsync(JsonSerializer.Serialize(value, typeInfo));

    /// <summary>Writes a plugin's details for a person, or as JSON with <c>--json</c>.</summary>
    /// <param name="context">The command.</param>
    /// <param name="detail">The plugin and its declaration.</param>
    /// <returns>A task that completes when it is written.</returns>
    internal static async Task PrintDetailAsync(CommandContext context, PluginDetail detail)
    {
        if (context.Arguments.Json)
        {
            await PrintAsync(context, PluginMapper.ToDetail(detail.Plugin, detail.Info), CliJsonContext.Default.PluginDetailResponse).ConfigureAwait(false);
            return;
        }

        await context.SayAsync($"{detail.Plugin.Id} {detail.Plugin.Version} ({SnakeCase.Of(detail.Plugin.Status)}, {detail.Plugin.Signer ?? "unsigned"})").ConfigureAwait(false);
        if (detail.Info is not { } info)
        {
            await context.SayAsync("Not enabled, so it was not asked what it declares.").ConfigureAwait(false);
            return;
        }

        await context.SayAsync("Kinds:").ConfigureAwait(false);
        foreach (KindInfo kind in info.Kinds)
        {
            string children = kind.Children.Count == 0 ? "leaf" : $"children: {string.Join(", ", kind.Children)}";
            string roles = kind.Roles.Count == 0 ? string.Empty : $"; roles: {string.Join(", ", kind.Roles)}";
            await context.SayAsync($"  {kind.Kind} ({kind.Label}): {children}{roles}").ConfigureAwait(false);
        }

        await context.SayAsync($"Filters: {(info.Filters.Count == 0 ? "none" : string.Join(", ", info.Filters.Select(filter => filter.Id)))}").ConfigureAwait(false);
        await context.SayAsync($"Sorts: {(info.Sorts.Count == 0 ? "none" : string.Join(", ", info.Sorts.Select(sort => sort.Value)))}").ConfigureAwait(false);
        await context.SayAsync($"Settings: {(info.Settings.Count == 0 ? "none" : string.Join(", ", info.Settings.Select(setting => setting.Key)))}").ConfigureAwait(false);
    }

    private static Task<int> ChangeAsync(CommandContext context, string id, Func<EmmaRuntime, PluginId, Task<bool>> change, string done) =>
        WithPluginAsync(
            context,
            id,
            async (runtime, plugin) =>
            {
                if (!await change(runtime, plugin).ConfigureAwait(false))
                {
                    return await context.FailAsync($"{id} is not installed.").ConfigureAwait(false);
                }

                await context.SayAsync(done).ConfigureAwait(false);
                return CommandContext.Success;
            });

    /// <summary>Runs a command that acts on one plugin: checks the id, opens EMMA, and disposes it afterwards.</summary>
    private static async Task<int> WithPluginAsync(CommandContext context, string id, Func<EmmaRuntime, PluginId, Task<int>> command)
    {
        Outcome<PluginId> plugin = RequestParsing.Plugin(id);
        return plugin.IsSuccess
            ? await WithRuntimeAsync(context, runtime => command(runtime, plugin.Value)).ConfigureAwait(false)
            : await context.MisuseAsync(plugin.Error.Message).ConfigureAwait(false);
    }

    /// <summary>Opens EMMA over the data directory, runs a command and disposes it, or reports why EMMA could not be opened.</summary>
    private static async Task<int> WithRuntimeAsync(CommandContext context, Func<EmmaRuntime, Task<int>> command)
    {
        Outcome<EmmaRuntime> opened = await context.OpenAsync().ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return await context.FailAsync(opened.Error).ConfigureAwait(false);
        }

        EmmaRuntime runtime = opened.Value;
        await using (runtime.ConfigureAwait(false))
        {
            return await command(runtime).ConfigureAwait(false);
        }
    }
}
