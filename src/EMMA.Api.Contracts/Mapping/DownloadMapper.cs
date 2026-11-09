using System.Diagnostics.CodeAnalysis;

using EMMA.Api.Contracts;
using EMMA.Domain;

namespace EMMA.Api.Mapping;

/// <summary>Maps download jobs and their leaves to the wire.</summary>
public static class DownloadMapper
{
    /// <summary>Maps a job.</summary>
    /// <param name="job">The job.</param>
    /// <returns>The wire form.</returns>
    public static DownloadDto ToDto(DownloadJob job) => new(
        job.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
        job.Node.ToString(),
        job.Role.Value,
        job.Hint,
        SnakeCase.Of(job.State),
        job.ItemsCompleted,
        job.ItemsTotal,
        job.BytesDownloaded,
        job.ErrorMessage,
        job.CreatedAt,
        job.UpdatedAt,
        job.StartedAt,
        job.CompletedAt);

    /// <summary>Maps a leaf.</summary>
    /// <param name="item">The leaf.</param>
    /// <returns>The wire form.</returns>
    public static DownloadItemDto ToDto(DownloadItem item) =>
        new(item.Node.ToString(), item.Position, item.IsStored, item.Mime, item.Bytes, item.ErrorMessage);

    /// <summary>Reads a job id.</summary>
    /// <param name="text">The id text.</param>
    /// <param name="name">The parameter name, for the error message.</param>
    /// <returns>The id, or why it is invalid.</returns>
    public static Outcome<Guid> ParseId(string? text, string name) =>
        Guid.TryParseExact(text, "D", out Guid id)
            ? Outcome.Success(id)
            : Outcome.Failure<Guid>(ErrorCode.InvalidArgument, $"'{name}' must be a download id.");

    /// <summary>Reads state names as the API spells them.</summary>
    /// <param name="texts">The names, such as <c>failed</c>; none means every state.</param>
    /// <returns>The states, <see langword="null"/> for none given, or why a name is not a state.</returns>
    public static Outcome<IReadOnlyCollection<DownloadState>?> ParseStates(IReadOnlyList<string?>? texts)
    {
        if (texts is null || texts.Count == 0)
        {
            return Outcome.Success<IReadOnlyCollection<DownloadState>?>(null);
        }

        List<DownloadState> states = [];
        foreach (string? text in texts)
        {
            if (!TryParseState(text, out DownloadState state))
            {
                return Outcome.Failure<IReadOnlyCollection<DownloadState>?>(
                    ErrorCode.InvalidArgument,
                    "'state' must be queued, running, paused, completed, failed or canceled.");
            }

            states.Add(state);
        }

        return Outcome.Success<IReadOnlyCollection<DownloadState>?>(states);
    }

    private static bool TryParseState([NotNullWhen(true)] string? text, out DownloadState state)
    {
        DownloadState[] match = [.. Enum.GetValues<DownloadState>().Where(candidate => string.Equals(text, SnakeCase.Of(candidate), StringComparison.Ordinal))];
        state = match.Length == 1 ? match[0] : default;
        return match.Length == 1;
    }
}
