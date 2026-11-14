using System.Globalization;

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Cli.Commands;

/// <summary>
/// Looks at what a player would be given for a node, and serves it to a real player. Both work on nodes the data
/// directory has already seen (browse or search to them first, with an app or the API).
/// </summary>
internal static class PlaybackCommands
{
    /// <summary>Shows the streams, subtitles, audio tracks and chapters of a node.</summary>
    /// <param name="context">The command.</param>
    /// <param name="reference">The node reference.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> PlanAsync(CommandContext context, string reference)
    {
        Outcome<NodeRef> node = RequestParsing.Node(reference, "ref");
        Outcome<string?> hint = RequestParsing.Hint(context.Arguments.Option("hint"));
        if (!node.IsSuccess || !hint.IsSuccess)
        {
            return context.MisuseAsync((node.Error ?? hint.Error)!.Message);
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                Outcome<PlaybackPlan> plan = await runtime.Playback.PlanAsync(node.Value, hint.Value, context.CancellationToken).ConfigureAwait(false);
                if (!plan.IsSuccess)
                {
                    return await context.FailAsync(plan.Error).ConfigureAwait(false);
                }

                if (context.Arguments.Json)
                {
                    await PluginCommands.PrintAsync(context, PlaybackMapper.ToDto(plan.Value), CliJsonContext.Default.PlaybackPlanResponse).ConfigureAwait(false);
                    return CommandContext.Success;
                }

                await DescribeAsync(context, plan.Value).ConfigureAwait(false);
                return CommandContext.Success;
            });
    }

    /// <summary>Opens a node's media on a local address and keeps serving it until interrupted, so any player can try it: <c>mpv &lt;address&gt;</c>.</summary>
    /// <param name="context">The command.</param>
    /// <param name="reference">The node reference.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> ServeAsync(CommandContext context, string reference)
    {
        Outcome<NodeRef> node = RequestParsing.Node(reference, "ref");
        Outcome<AssetRole> role = RequestParsing.Role(context.Arguments.Option("role") ?? PlaybackVocabulary.StreamRole);
        Outcome<string?> hint = RequestParsing.Hint(context.Arguments.Option("hint"));
        Outcome<int> index = ParseIndex(context.Arguments.Option("index"));
        if (!node.IsSuccess || !role.IsSuccess || !hint.IsSuccess || !index.IsSuccess)
        {
            return context.MisuseAsync((node.Error ?? role.Error ?? hint.Error ?? index.Error)!.Message);
        }

        return PluginCommands.WithRuntimeAsync(
            context,
            async runtime =>
            {
                Outcome<OpenedPlayback> opened = await runtime
                    .OpenStreamUrlAsync(node.Value, role.Value, index.Value, hint.Value, context.CancellationToken)
                    .ConfigureAwait(false);
                if (!opened.IsSuccess)
                {
                    return await context.FailAsync(opened.Error).ConfigureAwait(false);
                }

                if (context.Arguments.Json)
                {
                    await PluginCommands.PrintAsync(context, new StreamOpenResponse(opened.Value.Url, opened.Value.Mime), CliJsonContext.Default.StreamOpenResponse).ConfigureAwait(false);
                }
                else
                {
                    await context.SayAsync($"{opened.Value.Url} ({opened.Value.Mime})").ConfigureAwait(false);
                    await context.SayAsync("Serving until interrupted (Ctrl+C).").ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Interrupted: this is how the command ends.
                }

                return CommandContext.Success;
            });
    }

    private static Outcome<int> ParseIndex(string? text)
    {
        if (text is null)
        {
            return Outcome.Success(0);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? RequestParsing.Index(parsed)
            : Outcome.Failure<int>(ErrorCode.InvalidArgument, "'--index' must be a whole number.");
    }

    private static async Task DescribeAsync(CommandContext context, PlaybackPlan plan)
    {
        string length = plan.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : "unknown length";
        await context.SayAsync($"{plan.Node} plays as '{plan.Primary}' ({length})").ConfigureAwait(false);
        await ListAsync(context, "Sources", plan.Sources).ConfigureAwait(false);
        await ListAsync(context, "Subtitles", plan.Subtitles).ConfigureAwait(false);
        await ListAsync(context, "Audio tracks", plan.AudioTracks).ConfigureAwait(false);
        if (plan.Chapters.Count > 0)
        {
            await context.SayAsync("Chapters:").ConfigureAwait(false);
        }

        foreach (PlaybackChapter chapter in plan.Chapters)
        {
            await context.SayAsync($"  {TimeSpan.FromMilliseconds(chapter.StartMs).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)}  {chapter.Title}").ConfigureAwait(false);
        }
    }

    private static async Task ListAsync(CommandContext context, string heading, IReadOnlyList<PlaybackTrack> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        await context.SayAsync($"{heading}:").ConfigureAwait(false);
        foreach (PlaybackTrack track in tracks)
        {
            string detail = string.Join(
                ", ",
                new[]
                {
                    track.Label,
                    track.Quality,
                    track.Language,
                    track.Width is { } width && track.Height is { } height ? $"{width}x{height}" : null,
                    track.IsLive ? "live" : null,
                    track.DrmScheme is { } scheme ? $"protected ({scheme})" : null,
                    track.IsDefault ? "default" : null,
                }.Where(part => !string.IsNullOrEmpty(part)));
            await context.SayAsync($"  [{track.Index}] {track.Mime}{(detail.Length == 0 ? string.Empty : "  " + detail)}").ConfigureAwait(false);
        }
    }
}
