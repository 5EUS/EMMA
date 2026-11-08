using System.Text.Json;

using EMMA.Domain;
using EMMA.PluginHost.Signing;

namespace EMMA.Runtime;

/// <summary>Reads and validates a configuration document.</summary>
public static class RuntimeConfigReader
{
    /// <summary>Parses a configuration document.</summary>
    /// <param name="json">The UTF-8 JSON: <c>{"data_dir": "...", "user": "...", "trust": {"require_signed": true, "repositories": [...]}}</c>.</param>
    /// <param name="defaultDataDirectory">Used when the document has no <c>data_dir</c>; <see langword="null"/> makes <c>data_dir</c> required.</param>
    /// <returns>The settings, or why the document was refused.</returns>
    public static Outcome<RuntimeConfig> Parse(ReadOnlySpan<byte> json, string? defaultDataDirectory = null)
    {
        RuntimeConfigDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.RuntimeConfigDocument);
        }
        catch (JsonException ex)
        {
            return Refuse($"The configuration is not valid: {ex.Message}");
        }

        string? dataDirectory = string.IsNullOrWhiteSpace(document?.DataDir) ? defaultDataDirectory : document.DataDir;
        if (document is null || string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Refuse("The configuration needs a 'data_dir'.");
        }

        UserId user = RuntimeConfig.DefaultUser;
        if (document.User is not null && !UserId.TryCreate(document.User, out user))
        {
            return Refuse("The 'user' must be non-blank and at most 128 characters.");
        }

        Outcome<TrustPolicy> trust = ReadTrust(document.Trust);
        if (!trust.IsSuccess)
        {
            return Outcome.Failure<RuntimeConfig>(trust.Error);
        }

        string? nativeDirectory = document.NativeDir is null ? null : Path.GetFullPath(document.NativeDir);
        return Outcome.Success(new RuntimeConfig(Path.GetFullPath(dataDirectory), user, trust.Value, nativeDirectory));
    }

    private static Outcome<TrustPolicy> ReadTrust(TrustDocument? trust)
    {
        List<TrustedRepository> repositories = [];
        foreach (RepositoryDocument repository in trust?.Repositories ?? [])
        {
            if (string.IsNullOrWhiteSpace(repository.Id) || repository.RootKeys is not { Count: > 0 } keys || keys.Values.Any(string.IsNullOrWhiteSpace))
            {
                return Outcome.Failure<TrustPolicy>(ErrorCode.InvalidArgument, "Every trusted repository needs an 'id' and at least one non-empty entry in 'root_keys'.");
            }

            repositories.Add(new TrustedRepository(repository.Id, keys));
        }

        return Outcome.Success(TrustPolicy.Create(repositories, trust?.RequireSigned ?? true));
    }

    private static Outcome<RuntimeConfig> Refuse(string message) => Outcome.Failure<RuntimeConfig>(ErrorCode.InvalidArgument, message);
}
