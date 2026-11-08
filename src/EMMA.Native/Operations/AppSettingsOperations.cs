using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>What the app remembers for the handle's user: small text choices under a scope.</summary>
internal static class AppSettingsOperations
{
    /// <summary>Gets what is remembered under a scope.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> GetAsync(OperationContext context)
    {
        Outcome<AppSettingsCall> call = context.Read(NativeJsonContext.Default.AppSettingsCall);
        if (!call.IsSuccess)
        {
            return Envelope.Failure(call.Error);
        }

        return Values(call.Value.Scope, await context.Runtime.AppSettings.GetAsync(context.User, call.Value.Scope, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Changes what is remembered under a scope, all or none.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SetAsync(OperationContext context)
    {
        Outcome<AppSettingsCall> call = context.Read(NativeJsonContext.Default.AppSettingsCall);
        Outcome<IReadOnlyList<AppSettingChange>> changes = call.IsSuccess
            ? UserMapper.ToChanges(call.Value.Changes)
            : Outcome.Failure<IReadOnlyList<AppSettingChange>>(call.Error);
        if (!changes.IsSuccess)
        {
            return Envelope.Failure(changes.Error);
        }

        return Values(
            call.Value.Scope,
            await context.Runtime.AppSettings.SetAsync(context.User, call.Value.Scope, changes.Value, context.CancellationToken).ConfigureAwait(false));
    }

    private static NativeResponse Values(string? scope, Outcome<IReadOnlyDictionary<string, string>> values) => values.IsSuccess
        ? Envelope.Ok(new AppSettingsResponse(scope!, values.Value), ApiJsonContext.Default.AppSettingsResponse)
        : Envelope.Failure(values.Error);
}
