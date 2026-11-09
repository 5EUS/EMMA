using System.Globalization;

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;

namespace EMMA.Cli.Commands;

/// <summary>
/// Manages the download queue in a data directory. The queue is shared with the app and the HTTP API; a command
/// line that only changes the queue leaves the work to them, and <c>downloads run</c> does the work here.
/// </summary>
internal static class DownloadCommands
{
    /// <summary>Lists downloads.</summary>
    /// <param name="context">The command.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ListAsync(CommandContext context) =>
        PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                IReadOnlyList<DownloadJob> jobs = await runtime.Downloads.ListAsync(null, int.MaxValue, context.CancellationToken).ConfigureAwait(false);
                if (context.Arguments.Json)
                {
                    await PluginCommands.PrintAsync(context, new DownloadListResponse([.. jobs.Select(DownloadMapper.ToDto)]), CliJsonContext.Default.DownloadListResponse).ConfigureAwait(false);
                    return CommandContext.Success;
                }

                if (jobs.Count == 0)
                {
                    await context.SayAsync("No downloads.").ConfigureAwait(false);
                }

                foreach (DownloadJob job in jobs)
                {
                    await context.SayAsync(Describe(job)).ConfigureAwait(false);
                }

                return CommandContext.Success;
            });

    /// <summary>Queues a download.</summary>
    /// <param name="context">The command.</param>
    /// <param name="reference">The node reference.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> AddAsync(CommandContext context, string reference)
    {
        Outcome<NodeRef> node = RequestParsing.Node(reference, "ref");
        Outcome<AssetRole> role = RequestParsing.Role(context.Arguments.Option("role"));
        Outcome<string?> hint = RequestParsing.Hint(context.Arguments.Option("hint"));
        if (!node.IsSuccess || !role.IsSuccess || !hint.IsSuccess)
        {
            return context.MisuseAsync((node.Error ?? role.Error ?? hint.Error)!.Message + " (--role <role> is required.)");
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime => await ReportAsync(context, await runtime.Downloads.EnqueueAsync(node.Value, role.Value, hint.Value, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
    }

    /// <summary>Pauses, resumes, retries or cancels a download.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The download id.</param>
    /// <param name="change">Makes the change.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ChangeAsync(CommandContext context, string id, Func<DownloadPipeline, Guid, CancellationToken, Task<Outcome<DownloadJob>>> change)
    {
        Outcome<Guid> parsed = DownloadMapper.ParseId(id, "id");
        return parsed.IsSuccess
            ? PluginCommands.WithRuntimeAsync(
                context,
                async runtime => await ReportAsync(context, await change(runtime.Downloads, parsed.Value, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false))
            : context.MisuseAsync(parsed.Error.Message);
    }

    /// <summary>Deletes a download and its files.</summary>
    /// <param name="context">The command.</param>
    /// <param name="id">The download id.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> DeleteAsync(CommandContext context, string id)
    {
        Outcome<Guid> parsed = DownloadMapper.ParseId(id, "id");
        if (!parsed.IsSuccess)
        {
            return context.MisuseAsync(parsed.Error.Message);
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                if (!await runtime.Downloads.DeleteAsync(parsed.Value, context.CancellationToken).ConfigureAwait(false))
                {
                    return await context.FailAsync("That download does not exist.").ConfigureAwait(false);
                }

                await context.SayAsync("Deleted.").ConfigureAwait(false);
                return CommandContext.Success;
            });
    }

    /// <summary>Deletes the downloads in some states.</summary>
    /// <param name="context">The command.</param>
    /// <param name="states">The state names.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ClearAsync(CommandContext context, string[] states)
    {
        Outcome<IReadOnlyCollection<DownloadState>?> parsed = DownloadMapper.ParseStates(states);
        if (!parsed.IsSuccess)
        {
            return context.MisuseAsync(parsed.Error.Message);
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                Outcome<int> cleared = await runtime.Downloads.ClearAsync(parsed.Value ?? [], context.CancellationToken).ConfigureAwait(false);
                return cleared.IsSuccess
                    ? await CountAsync(context, cleared.Value, "Deleted {0} download(s).", CliJsonContext.Default.DownloadCountResponse).ConfigureAwait(false)
                    : await context.FailAsync(cleared.Error).ConfigureAwait(false);
            });
    }

    /// <summary>Queues every failed download again.</summary>
    /// <param name="context">The command.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RetryFailedAsync(CommandContext context) =>
        PluginCommands.WithRuntimeAsync(
            context,
            async runtime => await CountAsync(
                context,
                await runtime.Downloads.RetryFailedAsync(context.CancellationToken).ConfigureAwait(false),
                "Queued {0} failed download(s) again.",
                CliJsonContext.Default.DownloadCountResponse).ConfigureAwait(false));

    /// <summary>Shows or sets how many downloads run at once.</summary>
    /// <param name="context">The command.</param>
    /// <param name="count">The new limit, or <see langword="null"/> to show it.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ParallelAsync(CommandContext context, string? count)
    {
        int parallel = 0;
        if (count is not null && !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out parallel))
        {
            return context.MisuseAsync("The number of downloads must be a whole number.");
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                Outcome<int> current = count is null
                    ? Outcome.Success(await runtime.Downloads.GetParallelAsync(context.CancellationToken).ConfigureAwait(false))
                    : await runtime.Downloads.SetParallelAsync(parallel, context.CancellationToken).ConfigureAwait(false);
                if (!current.IsSuccess)
                {
                    return await context.FailAsync(current.Error).ConfigureAwait(false);
                }

                if (context.Arguments.Json)
                {
                    await PluginCommands.PrintAsync(context, new DownloadSettingsResponse(current.Value), CliJsonContext.Default.DownloadSettingsResponse).ConfigureAwait(false);
                }
                else
                {
                    await context.SayAsync($"{current.Value} download(s) run at once.").ConfigureAwait(false);
                }

                return CommandContext.Success;
            });
    }

    /// <summary>Works through the queue, then reports what did not finish.</summary>
    /// <param name="context">The command.</param>
    /// <returns>The exit code: failed when any download is left failed.</returns>
    public static Task<int> RunAsync(CommandContext context) =>
        PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                await runtime.Downloads.RunUntilIdleAsync(context.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<DownloadJob> failed = await runtime.Downloads.ListAsync([DownloadState.Failed], int.MaxValue, context.CancellationToken).ConfigureAwait(false);
                foreach (DownloadJob job in failed)
                {
                    await context.SayAsync(Describe(job)).ConfigureAwait(false);
                }

                await context.SayAsync(failed.Count == 0 ? "The queue is empty." : $"{failed.Count} download(s) failed.").ConfigureAwait(false);
                return failed.Count == 0 ? CommandContext.Success : CommandContext.Failed;
            });

    private static async Task<int> ReportAsync(CommandContext context, Outcome<DownloadJob> job)
    {
        if (!job.IsSuccess)
        {
            return await context.FailAsync(job.Error).ConfigureAwait(false);
        }

        if (context.Arguments.Json)
        {
            await PluginCommands.PrintAsync(context, DownloadMapper.ToDto(job.Value), CliJsonContext.Default.DownloadDto).ConfigureAwait(false);
        }
        else
        {
            await context.SayAsync(Describe(job.Value)).ConfigureAwait(false);
        }

        return CommandContext.Success;
    }

    private static async Task<int> CountAsync(CommandContext context, int count, string message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<DownloadCountResponse> typeInfo)
    {
        if (context.Arguments.Json)
        {
            await PluginCommands.PrintAsync(context, new DownloadCountResponse(count), typeInfo).ConfigureAwait(false);
        }
        else
        {
            await context.SayAsync(string.Format(CultureInfo.InvariantCulture, message, count)).ConfigureAwait(false);
        }

        return CommandContext.Success;
    }

    private static string Describe(DownloadJob job)
    {
        string progress = job.ItemsTotal == 0 ? "-" : $"{job.ItemsCompleted}/{job.ItemsTotal}";
        string error = job.ErrorMessage is null ? string.Empty : $"  {job.ErrorMessage}";
        return string.Create(CultureInfo.InvariantCulture, $"{job.Id:D}  {SnakeCase.Of(job.State),-10} {progress,9}  {job.Node} ({job.Role}){error}");
    }
}
