using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;
using EMMA.Runtime;

namespace EMMA.Native.Operations;

/// <summary>
/// Installed plugins. Unlike the HTTP API, the library lets its caller install and remove plugins: the caller is
/// the app that embeds EMMA, in its own process, so it already has every authority these operations grant.
/// </summary>
internal static class PluginOperations
{
    /// <summary>Lists installed plugins.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ListAsync(OperationContext context)
    {
        IReadOnlyList<InstalledPlugin> plugins = await context.Runtime.Registry.ListAsync(context.CancellationToken).ConfigureAwait(false);
        return Envelope.Ok(new PluginListResponse([.. plugins.Select(PluginMapper.ToSummary)]), ApiJsonContext.Default.PluginListResponse);
    }

    /// <summary>Gets one plugin and what it declares.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> GetAsync(OperationContext context)
    {
        Outcome<PluginId> id = ReadPlugin(context);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        Outcome<PluginDetail> detail = await context.Runtime.DescribePluginAsync(id.Value, context.CancellationToken).ConfigureAwait(false);
        return detail.IsSuccess
            ? Envelope.Ok(PluginMapper.ToDetail(detail.Value.Plugin, detail.Value.Info), ApiJsonContext.Default.PluginDetailResponse)
            : Envelope.Failure(detail.Error);
    }

    /// <summary>Installs a plugin from a package file.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> InstallAsync(OperationContext context)
    {
        Outcome<InstallCall> call = context.Read(NativeJsonContext.Default.InstallCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        if (string.IsNullOrWhiteSpace(call.Value.PackagePath) || string.IsNullOrWhiteSpace(call.Value.Repository))
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'package_path' and 'repository' are required.");
        }

        try
        {
            FileStream package = new(call.Value.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            await using (package.ConfigureAwait(false))
            {
                Outcome<InstalledPlugin> installed = await context.Runtime.InstallAsync(package, call.Value.Repository, context.CancellationToken).ConfigureAwait(false);
                return installed.IsSuccess
                    ? Envelope.Ok(PluginMapper.ToSummary(installed.Value), ApiJsonContext.Default.PluginSummary)
                    : Envelope.Failure(installed.Error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "The package file cannot be read.");
        }
    }

    /// <summary>Enables, disables or quarantines a plugin.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SetStatusAsync(OperationContext context)
    {
        Outcome<StatusCall> call = context.Read(NativeJsonContext.Default.StatusCall);
        Outcome<PluginId> id = call.IsSuccess ? RequestParsing.Plugin(call.Value.Plugin) : Outcome.Failure<PluginId>(call.Error);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        PluginStatus? status = call.Value.Status switch
        {
            "enabled" => PluginStatus.Enabled,
            "disabled" => PluginStatus.Disabled,
            "quarantined" => PluginStatus.Quarantined,
            _ => null,
        };
        if (status is not { } chosen)
        {
            return Envelope.Failure(ErrorCode.InvalidArgument, "'status' must be 'enabled', 'disabled' or 'quarantined'.");
        }

        return await context.Runtime.SetStatusAsync(id.Value, chosen, context.CancellationToken).ConfigureAwait(false)
            ? Envelope.Done
            : Envelope.Failure(ErrorCode.NotFound, "That plugin is not installed.");
    }

    /// <summary>Removes a plugin and its files.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> UninstallAsync(OperationContext context)
    {
        Outcome<PluginId> id = ReadPlugin(context);
        if (!id.IsSuccess)
        {
            return Envelope.Failure(id.Error);
        }

        return await context.Runtime.UninstallAsync(id.Value, context.CancellationToken).ConfigureAwait(false)
            ? Envelope.Done
            : Envelope.Failure(ErrorCode.NotFound, "That plugin is not installed.");
    }

    private static Outcome<PluginId> ReadPlugin(OperationContext context)
    {
        Outcome<PluginCall> call = context.Read(NativeJsonContext.Default.PluginCall);
        return call.IsSuccess ? RequestParsing.Plugin(call.Value.Plugin) : Outcome.Failure<PluginId>(call.Error);
    }
}
