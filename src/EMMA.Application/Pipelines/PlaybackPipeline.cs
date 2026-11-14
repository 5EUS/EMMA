using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Gathers what a player needs to play a node: the streams or audio files it resolves to, subtitles, alternate audio
/// tracks and chapter markers. Which of these exist comes from the roles the plugin declared for the node's kind, so the
/// pipeline never asks what kind of media a node is. See <see cref="PlaybackVocabulary"/> for the profile it reads.
/// </summary>
/// <param name="catalog">Gives what each plugin declares.</param>
/// <param name="assets">Resolves nodes to assets.</param>
/// <param name="store">Remembers the nodes the host has served; a plan can only be made for one of them.</param>
public sealed class PlaybackPipeline(CatalogPipeline catalog, AssetPipeline assets, INodeStorePort store)
{
    private static readonly AssetRole Stream = AssetRole.Create(PlaybackVocabulary.StreamRole);
    private static readonly AssetRole Audio = AssetRole.Create(PlaybackVocabulary.AudioRole);
    private static readonly AssetRole Subtitle = AssetRole.Create(PlaybackVocabulary.SubtitleRole);
    private static readonly AssetRole Chapters = AssetRole.Create(PlaybackVocabulary.ChaptersRole);

    /// <summary>Makes the plan for a node.</summary>
    /// <param name="node">The node; the host must have seen it.</param>
    /// <param name="hint">A quality preference passed to the plugin when it resolves the node's own media.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan, or why the node cannot be played.</returns>
    public async Task<Outcome<PlaybackPlan>> PlanAsync(NodeRef node, string? hint, CancellationToken cancellationToken)
    {
        Node? known = await store.GetAsync(node, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            return Outcome.Failure<PlaybackPlan>(ErrorCode.NotFound, "That node has not been seen; browse or search to it first.");
        }

        Outcome<PluginInfo> info = await catalog.DescribeAsync(node.Plugin, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return Outcome.Failure<PlaybackPlan>(info.Error);
        }

        IReadOnlyList<AssetRole> roles = info.Value.FindKind(known.Kind)?.Roles ?? [];
        AssetRole main;
        if (roles.Contains(Stream))
        {
            main = Stream;
        }
        else if (roles.Contains(Audio))
        {
            main = Audio;
        }
        else
        {
            return Outcome.Failure<PlaybackPlan>(ErrorCode.Unsupported, $"Nodes of kind '{known.Kind}' have nothing to play.");
        }

        Outcome<IReadOnlyList<Asset>> sources = await assets.ResolveAsync(node, main, AssetHint.ToProps(hint), cancellationToken).ConfigureAwait(false);
        if (!sources.IsSuccess)
        {
            return Outcome.Failure<PlaybackPlan>(sources.Error);
        }

        // Everything else is optional: a subtitle list that cannot be had must not stop the video.
        IReadOnlyList<Asset> subtitles = roles.Contains(Subtitle) ? await this.OptionalAsync(node, Subtitle, cancellationToken).ConfigureAwait(false) : [];
        IReadOnlyList<Asset> alternates = main == Stream && roles.Contains(Audio) ? await this.OptionalAsync(node, Audio, cancellationToken).ConfigureAwait(false) : [];
        IReadOnlyList<Asset> markers = roles.Contains(Chapters) ? await this.OptionalAsync(node, Chapters, cancellationToken).ConfigureAwait(false) : [];
        Outcome<IReadOnlyList<PlaybackChapter>> chapters = PlaybackProfile.Chapters(node.Plugin, markers);

        IReadOnlyList<PlaybackTrack> tracks = PlaybackProfile.Tracks(main, sources.Value);
        IReadOnlyList<PlaybackChapter> chapterList = chapters.IsSuccess ? chapters.Value : [];
        long? duration = tracks.Select(track => track.DurationMs).FirstOrDefault(length => length is not null) ?? chapterList.Select(chapter => chapter.EndMs).LastOrDefault(end => end is not null);
        return Outcome.Success(new PlaybackPlan(
            node,
            main,
            tracks,
            PlaybackProfile.Tracks(Subtitle, subtitles),
            PlaybackProfile.Tracks(Audio, alternates),
            chapterList,
            duration));
    }

    private async Task<IReadOnlyList<Asset>> OptionalAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(node, role, [], cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess ? resolved.Value : [];
    }
}
