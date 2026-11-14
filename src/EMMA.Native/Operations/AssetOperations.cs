using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;
using EMMA.Runtime;

namespace EMMA.Native.Operations;

/// <summary>
/// Resolves nodes to assets and reads their bytes. As over HTTP, a listing says how an asset is delivered but
/// never where it lives; the caller asks for an asset by index and the host fetches it under the plugin's policy.
/// </summary>
internal static class AssetOperations
{
    /// <summary>Lists what a node offers for a role.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ListAsync(OperationContext context)
    {
        Outcome<AssetCall> call = context.Read(NativeJsonContext.Default.AssetCall);
        Outcome<(NodeRef Node, AssetRole Role, string? Hint)> target = ReadTarget(call);
        if (!target.IsSuccess)
        {
            return Envelope.Failure(target.Error);
        }

        Outcome<IReadOnlyList<Asset>> resolved = await context.Runtime.Assets
            .ResolveAsync(target.Value.Node, target.Value.Role, AssetHint.ToProps(target.Value.Hint), context.CancellationToken)
            .ConfigureAwait(false);
        return resolved.IsSuccess
            ? Envelope.Ok(AssetMapper.ToResponse(resolved.Value), ApiJsonContext.Default.AssetListResponse)
            : Envelope.Failure(resolved.Error);
    }

    /// <summary>Reads the bytes of one asset. The JSON describes them; the bytes arrive in the blob.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ContentAsync(OperationContext context)
    {
        Outcome<AssetCall> call = context.Read(NativeJsonContext.Default.AssetCall);
        Outcome<(NodeRef Node, AssetRole Role, string? Hint)> target = ReadTarget(call);
        if (!target.IsSuccess)
        {
            return Envelope.Failure(target.Error);
        }

        Outcome<int> index = RequestParsing.Index(call.Value.Index);
        if (!index.IsSuccess)
        {
            return Envelope.Failure(index.Error);
        }

        ByteRange? range = null;
        if (call.Value.Range is { } requested)
        {
            if (requested.Start is not >= 0 || requested.Length is <= 0)
            {
                return Envelope.Failure(ErrorCode.InvalidArgument, "'range' needs a 'start' of zero or more and, if given, a positive 'length'.");
            }

            range = new ByteRange(requested.Start.Value, requested.Length);
        }

        Outcome<AssetBytes> content = await context.Runtime
            .ReadAssetAsync(target.Value.Node, target.Value.Role, index.Value, range, target.Value.Hint, context.CancellationToken)
            .ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return Envelope.Failure(content.Error);
        }

        AssetBytes bytes = content.Value;
        if (bytes.RangeNotSatisfiable)
        {
            return Envelope.Fault("range_not_satisfiable", "The range starts beyond the end of the asset.");
        }

        ContentInfo info = new(bytes.Mime, bytes.Bytes.Length, bytes.RangeStart, bytes.Total, bytes.AcceptsRanges);
        return Envelope.Ok(info, NativeJsonContext.Default.ContentInfo, bytes.Bytes.ToArray());
    }

    /// <summary>Reads a node's thumbnail through the host. The JSON describes it; the bytes arrive in the blob.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ThumbnailAsync(OperationContext context)
    {
        Outcome<RefCall> call = context.Read(NativeJsonContext.Default.RefCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        Outcome<AssetBytes> image = await context.Runtime.ReadThumbnailAsync(node.Value, context.CancellationToken).ConfigureAwait(false);
        if (!image.IsSuccess)
        {
            return Envelope.Failure(image.Error);
        }

        ContentInfo info = new(image.Value.Mime, image.Value.Bytes.Length, RangeStart: null, Total: null, AcceptsRanges: false);
        return Envelope.Ok(info, NativeJsonContext.Default.ContentInfo, image.Value.Bytes.ToArray());
    }

    /// <summary>Empties the asset cache and the stored child listings; nodes the host has seen are kept.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ClearCacheAsync(OperationContext context)
    {
        await context.Runtime.ClearCacheAsync(context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new CacheClearedResponse(Cleared: true), ApiJsonContext.Default.CacheClearedResponse);
    }

    private static Outcome<(NodeRef Node, AssetRole Role, string? Hint)> ReadTarget(Outcome<AssetCall> call)
    {
        if (!call.IsSuccess)
        {
            return Outcome.Failure<(NodeRef, AssetRole, string?)>(call.Error);
        }

        Outcome<NodeRef> node = RequestParsing.Node(call.Value.Ref, "ref");
        Outcome<AssetRole> role = RequestParsing.Role(call.Value.Role);
        Outcome<string?> hint = RequestParsing.Hint(call.Value.Hint);
        return node.IsSuccess && role.IsSuccess && hint.IsSuccess
            ? Outcome.Success((node.Value, role.Value, hint.Value))
            : Outcome.Failure<(NodeRef, AssetRole, string?)>((node.Error ?? role.Error ?? hint.Error)!);
    }
}
