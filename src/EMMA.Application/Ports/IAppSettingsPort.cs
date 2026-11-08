using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Small choices a user wants remembered, as text under a scope (a series, the history, the app): not a plugin's
/// settings, which the plugin declares, but the app's own memory, such as "read this series newest first".
/// </summary>
public interface IAppSettingsPort
{
    /// <summary>Gets every choice stored under a scope.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values by key; empty when nothing is stored.</returns>
    Task<IReadOnlyDictionary<string, string>> GetAsync(UserId user, string scope, CancellationToken cancellationToken);

    /// <summary>Applies changes under a scope, all or none.</summary>
    /// <param name="user">The owner.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="changes">The changes; a change with no value forgets the choice.</param>
    /// <param name="updatedAt">When the changes are made.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the changes are stored.</returns>
    Task ApplyAsync(UserId user, string scope, IReadOnlyList<AppSettingChange> changes, DateTimeOffset updatedAt, CancellationToken cancellationToken);
}
