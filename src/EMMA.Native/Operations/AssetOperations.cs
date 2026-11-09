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
    private const int MaxIndex = 1023;

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

        int index = call.Value.Index ?? 0;
        if (index is < 0 or > MaxIndex)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, $"'index' must be a whole number from 0 to {MaxIndex}.");
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
            .ReadAssetAsync(target.Value.Node, target.Value.Role, index, range, target.Value.Hint, context.CancellationToken)
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
