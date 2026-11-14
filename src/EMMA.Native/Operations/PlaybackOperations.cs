using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;
using EMMA.Runtime;

namespace EMMA.Native.Operations;

/// <summary>
/// What an app's player needs: the plan for a node, and an address on this device to play one of its assets from. As over
/// HTTP, a plan says what exists but never where it lives; the host fetches the media under the plugin's policy.
/// </summary>
internal static class PlaybackOperations
{
    /// <summary>Lists the streams, subtitles, audio tracks and chapters of a node.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> PlanAsync(OperationContext context)
    {
        Outcome<PlanCall> call = context.Read(NativeJsonContext.Default.PlanCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        Outcome<string?> hint = call.IsSuccess ? RequestParsing.Hint(call.Value.Hint) : Outcome.Failure<string?>(call.Error);
        if (!node.IsSuccess || !hint.IsSuccess)
        {
            return Envelope.Failure((node.Error ?? hint.Error)!);
        }

        Outcome<PlaybackPlan> plan = await context.Runtime.Playback.PlanAsync(node.Value, hint.Value, context.CancellationToken).ConfigureAwait(false);
        return plan.IsSuccess
            ? Envelope.Ok(PlaybackMapper.ToDto(plan.Value), ApiJsonContext.Default.PlaybackPlanResponse)
            : Envelope.Failure(plan.Error);
    }

    /// <summary>Opens an asset for playing and returns the address to give a player.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> OpenAsync(OperationContext context)
    {
        Outcome<StreamCall> call = context.Read(NativeJsonContext.Default.StreamCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        Outcome<NodeRef> node = RequestParsing.Node(call.Value.Ref, "ref");
        Outcome<AssetRole> role = RequestParsing.Role(call.Value.Role);
        Outcome<int> index = RequestParsing.Index(call.Value.Index);
        Outcome<string?> hint = RequestParsing.Hint(call.Value.Hint);
        if (!node.IsSuccess || !role.IsSuccess || !index.IsSuccess || !hint.IsSuccess)
        {
            return Envelope.Failure((node.Error ?? role.Error ?? index.Error ?? hint.Error)!);
        }

        Outcome<OpenedPlayback> opened = await context.Runtime
            .OpenStreamUrlAsync(node.Value, role.Value, index.Value, hint.Value, context.CancellationToken)
            .ConfigureAwait(false);
        return opened.IsSuccess
            ? Envelope.Ok(new StreamOpenResponse(opened.Value.Url, opened.Value.Mime), ApiJsonContext.Default.StreamOpenResponse)
            : Envelope.Failure(opened.Error);
    }
}
