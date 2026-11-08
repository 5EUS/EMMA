using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Cli.Commands;

/// <summary>Shows and changes a plugin's settings: the same values the app's settings screen edits, kept in the same data directory.</summary>
internal static class SettingsCommands
{
    /// <summary>Shows what a plugin declares as settings and what each is set to.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ShowAsync(CommandContext context, string id) =>
        PluginCommands.WithPluginAsync(
            context,
            id,
            async (runtime, plugin) =>
            {
                Outcome<(PluginInfo Info, IReadOnlyList<SettingState> States)> read = await ReadAsync(runtime, plugin, context.CancellationToken).ConfigureAwait(false);
                if (!read.IsSuccess)
                {
                    return await context.FailAsync(read.Error).ConfigureAwait(false);
                }

                if (context.Arguments.Json)
                {
                    await PluginCommands.PrintAsync(context, SettingsMapper.ToResponse(read.Value.States), CliJsonContext.Default.PluginSettingsResponse).ConfigureAwait(false);
                    return CommandContext.Success;
                }

                if (read.Value.Info.Settings.Count == 0)
                {
                    await context.SayAsync($"{id} declares no settings.").ConfigureAwait(false);
                }

                foreach (SettingSpec spec in read.Value.Info.Settings)
                {
                    SettingState state = read.Value.States.First(candidate => string.Equals(candidate.Key, spec.Key, StringComparison.Ordinal));
                    await context.SayAsync($"{spec.Key,-28} {SnakeCase.Of(spec.Type),-13} {Describe(spec, state)}").ConfigureAwait(false);
                }

                return CommandContext.Success;
            });

    /// <summary>Sets a setting, or clears it when no values are given.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="key">The setting key.</param>
    /// <param name="values">The new values; none clears.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> SetAsync(CommandContext context, string id, string key, string[] values) =>
        ChangeAsync(
            context,
            id,
            key,
            async (runtime, plugin, spec) =>
            {
                if (spec.Type == SettingType.Secret && values.Length > 0)
                {
                    // A value on the command line ends up in shell history and in the process list.
                    return await context.MisuseAsync($"'{key}' is a secret, so its value is not taken on the command line. Use: emma plugin settings {id} set-secret {key}").ConfigureAwait(false);
                }

                return await ApplyAsync(context, runtime, plugin, key, values).ConfigureAwait(false);
            });

    /// <summary>Sets a secret setting from the first line of standard input.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The plugin id.</param>
    /// <param name="key">The setting key.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> SetSecretAsync(CommandContext context, string id, string key) =>
        ChangeAsync(
            context,
            id,
            key,
            async (runtime, plugin, spec) =>
            {
                if (spec.Type != SettingType.Secret)
                {
                    return await context.MisuseAsync($"'{key}' is not a secret; use: emma plugin settings {id} set {key} <value>").ConfigureAwait(false);
                }

                string? line = await context.Input.ReadLineAsync(context.CancellationToken).ConfigureAwait(false);
                return string.IsNullOrEmpty(line)
                    ? await context.MisuseAsync("Give the secret as the first line of standard input.").ConfigureAwait(false)
                    : await ApplyAsync(context, runtime, plugin, key, [line]).ConfigureAwait(false);
            });

    private static Task<int> ChangeAsync(CommandContext context, string id, string key, Func<EmmaRuntime, PluginId, SettingSpec, Task<int>> change) =>
        PluginCommands.WithPluginAsync(
            context,
            id,
            async (runtime, plugin) =>
            {
                Outcome<(PluginInfo Info, IReadOnlyList<SettingState> States)> read = await ReadAsync(runtime, plugin, context.CancellationToken).ConfigureAwait(false);
                if (!read.IsSuccess)
                {
                    return await context.FailAsync(read.Error).ConfigureAwait(false);
                }

                SettingSpec? spec = read.Value.Info.Settings.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
                return spec is null
                    ? await context.FailAsync($"{id} declares no setting '{key}'.").ConfigureAwait(false)
                    : await change(runtime, plugin, spec).ConfigureAwait(false);
            });

    private static async Task<int> ApplyAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, string key, string[] values)
    {
        Outcome<IReadOnlyList<SettingState>> changed = await runtime.SetPluginSettingsAsync(plugin, [new SettingChange(key, values)], context.CancellationToken).ConfigureAwait(false);
        if (!changed.IsSuccess)
        {
            return await context.FailAsync(changed.Error).ConfigureAwait(false);
        }

        await context.SayAsync(values.Length == 0 ? $"Cleared {key}; its default applies." : $"Set {key}.").ConfigureAwait(false);
        return CommandContext.Success;
    }

    private static async Task<Outcome<(PluginInfo Info, IReadOnlyList<SettingState> States)>> ReadAsync(EmmaRuntime runtime, PluginId plugin, CancellationToken cancellationToken)
    {
        Outcome<PluginDetail> detail = await runtime.DescribePluginAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            return Outcome.Failure<(PluginInfo, IReadOnlyList<SettingState>)>(detail.Error);
        }

        if (detail.Value.Info is not { } info)
        {
            return Outcome.Failure<(PluginInfo, IReadOnlyList<SettingState>)>(ErrorCode.Unsupported, "The plugin is not enabled, so it cannot say what it declares. Enable it first.");
        }

        Outcome<IReadOnlyList<SettingState>> states = await runtime.GetPluginSettingsAsync(plugin, cancellationToken).ConfigureAwait(false);
        return states.IsSuccess
            ? Outcome.Success((info, states.Value))
            : Outcome.Failure<(PluginInfo, IReadOnlyList<SettingState>)>(states.Error);
    }

    private static string Describe(SettingSpec spec, SettingState state)
    {
        string value;
        if (state.IsSet)
        {
            value = spec.Type == SettingType.Secret ? "set" : string.Join(", ", state.Values);
        }
        else
        {
            value = spec.DefaultValues.Count > 0 ? $"{string.Join(", ", spec.DefaultValues)} (default)" : "(not set)";
        }

        return state.Missing ? $"{value} REQUIRED" : value;
    }
}
