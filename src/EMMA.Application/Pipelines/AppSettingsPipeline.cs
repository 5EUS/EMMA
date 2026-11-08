using System.Text.RegularExpressions;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// The app's own memory for a user: small text values under a scope, such as "this series is read newest first".
/// Scopes and keys are free-form so a client can remember what it likes, within limits that keep a client from
/// filling the database; the host keeps scopes that start with <see cref="ReservedPrefix"/> for itself and refuses
/// them here (the history retention is one, with its own operations).
/// </summary>
/// <param name="store">Where the values are kept.</param>
/// <param name="time">The time source that stamps each change.</param>
public sealed partial class AppSettingsPipeline(IAppSettingsPort store, TimeProvider time)
{
    /// <summary>The start of a scope name the host keeps for itself.</summary>
    public const string ReservedPrefix = "emma.";

    /// <summary>The longest scope, in characters. A node reference fits with room for a prefix.</summary>
    public const int MaxScopeLength = 640;

    /// <summary>The longest value, in characters.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The most keys under one scope.</summary>
    public const int MaxKeysPerScope = 100;

    /// <summary>The most changes in one call.</summary>
    public const int MaxChangesPerCall = 50;

    /// <summary>Gets what is remembered under a scope.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values by key, or why the scope was refused.</returns>
    public async Task<Outcome<IReadOnlyDictionary<string, string>>> GetAsync(UserId user, string? scope, CancellationToken cancellationToken)
    {
        string? problem = ScopeProblem(scope);
        return problem is null
            ? Outcome.Success(await store.GetAsync(user, scope!, cancellationToken).ConfigureAwait(false))
            : Outcome.Failure<IReadOnlyDictionary<string, string>>(ErrorCode.InvalidArgument, problem);
    }

    /// <summary>Changes what is remembered under a scope, all or none.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="changes">The changes; a change with no value forgets its key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What is remembered afterwards, or why nothing was changed.</returns>
    public async Task<Outcome<IReadOnlyDictionary<string, string>>> SetAsync(
        UserId user,
        string? scope,
        IReadOnlyList<AppSettingChange> changes,
        CancellationToken cancellationToken)
    {
        string? problem = ScopeProblem(scope) ?? ChangesProblem(changes);
        if (problem is not null)
        {
            return Outcome.Failure<IReadOnlyDictionary<string, string>>(ErrorCode.InvalidArgument, problem);
        }

        IReadOnlyDictionary<string, string> current = await store.GetAsync(user, scope!, cancellationToken).ConfigureAwait(false);
        int keys = current.Keys.Union(changes.Where(change => change.Value is not null).Select(change => change.Key), StringComparer.Ordinal).Count()
            - changes.Count(change => change.Value is null && current.ContainsKey(change.Key));
        if (keys > MaxKeysPerScope)
        {
            return Outcome.Failure<IReadOnlyDictionary<string, string>>(ErrorCode.InvalidArgument, $"A scope holds at most {MaxKeysPerScope} keys.");
        }

        if (changes.Count > 0)
        {
            await store.ApplyAsync(user, scope!, changes, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return Outcome.Success(await store.GetAsync(user, scope!, cancellationToken).ConfigureAwait(false));
    }

    private static string? ScopeProblem(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Length > MaxScopeLength || scope.Any(char.IsControl))
        {
            return $"'scope' must be 1 to {MaxScopeLength} characters without control characters.";
        }

        return scope.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"Scopes starting with '{ReservedPrefix}' belong to the host."
            : null;
    }

    private static string? ChangesProblem(IReadOnlyList<AppSettingChange> changes)
    {
        if (changes.Count > MaxChangesPerCall)
        {
            return $"A call changes at most {MaxChangesPerCall} keys.";
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AppSettingChange change in changes)
        {
            if (!KeyPattern().IsMatch(change.Key))
            {
                return "A key must be 1 to 64 lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.";
            }

            if (!seen.Add(change.Key))
            {
                return $"The key '{change.Key}' is changed twice.";
            }

            if (change.Value is { } value && (value.Length > MaxValueLength || value.Contains('\0', StringComparison.Ordinal)))
            {
                return $"A value must be at most {MaxValueLength} characters and hold no NUL.";
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyPattern();
}
