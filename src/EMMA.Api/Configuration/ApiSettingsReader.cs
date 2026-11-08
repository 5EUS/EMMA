using System.Globalization;

using EMMA.Domain;
using EMMA.PluginHost.Signing;

namespace EMMA.Api.Configuration;

/// <summary>
/// Reads <see cref="ApiSettings"/> from configuration by hand. Reflection-based binding is off the table under
/// NativeAOT, and reading explicitly lets every value be checked, so a typo fails the start rather than
/// silently falling back to a default that is weaker than intended.
/// </summary>
internal static class ApiSettingsReader
{
    private const string Root = "Emma";

    /// <summary>Reads and validates the settings.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="allowUnsignedPlugins">Whether <c>Emma:Trust:RequireSigned=false</c> is honoured. Only a development host passes <see langword="true"/>.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ApiConfigurationException">A value is invalid.</exception>
    public static ApiSettings Read(IConfiguration configuration, bool allowUnsignedPlugins)
    {
        string dataDirectory = ReadDataDirectory(configuration);
        return new ApiSettings(dataDirectory, ReadTrust(configuration, allowUnsignedPlugins), ReadSecurity(configuration));
    }

    private static string ReadDataDirectory(IConfiguration configuration)
    {
        string? configured = configuration[$"{Root}:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length == 0
            ? throw new ApiConfigurationException("Set Emma:DataDirectory: this account has no local application data folder.")
            : Path.Combine(local, "emma");
    }

    private static TrustSettings ReadTrust(IConfiguration configuration, bool allowUnsignedPlugins)
    {
        bool requireSigned = ReadBool(configuration, $"{Root}:Trust:RequireSigned", defaultValue: true);
        if (!requireSigned && !allowUnsignedPlugins)
        {
            throw new ApiConfigurationException("Emma:Trust:RequireSigned=false is only honoured in the Development environment.");
        }

        List<TrustedRepository> repositories = [];
        foreach (IConfigurationSection section in configuration.GetSection($"{Root}:Trust:Repositories").GetChildren())
        {
            string id = Required(section, "Id");
            Dictionary<string, string> keys = section.GetSection("RootKeys").GetChildren()
                .Where(key => !string.IsNullOrWhiteSpace(key.Value))
                .ToDictionary(key => key.Key, key => key.Value!, StringComparer.Ordinal);
            if (keys.Count == 0)
            {
                throw new ApiConfigurationException($"Trusted repository '{id}' has no RootKeys.");
            }

            repositories.Add(new TrustedRepository(id, keys));
        }

        return new TrustSettings(requireSigned, repositories);
    }

    private static SecuritySettings ReadSecurity(IConfiguration configuration)
    {
        string header = configuration[$"{Root}:Api:KeyHeader"] ?? "x-api-key";
        if (header.Length == 0 || !header.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new ApiConfigurationException("Emma:Api:KeyHeader must be a header name of letters, digits and hyphens.");
        }

        List<ApiClientKey> keys = [];
        foreach (IConfigurationSection section in configuration.GetSection($"{Root}:Api:Keys").GetChildren())
        {
            string clientId = Required(section, "ClientId");
            string? hashText = section["KeyHash"];
            if (!UserId.TryCreate(clientId, out _))
            {
                throw new ApiConfigurationException($"API client id '{clientId}' must be non-blank and at most 128 characters.");
            }

            if (!Security.ApiKeys.TryParseHash(hashText, out byte[]? hash))
            {
                throw new ApiConfigurationException($"The key hash for client '{clientId}' must be 'sha256:' and 64 hex digits. Run the host with --new-api-key to make one; never configure the key itself.");
            }

            if (keys.Exists(existing => string.Equals(existing.ClientId, clientId, StringComparison.Ordinal) || existing.KeyHash.AsSpan().SequenceEqual(hash)))
            {
                throw new ApiConfigurationException($"API client '{clientId}' repeats another client's id or key.");
            }

            keys.Add(new ApiClientKey(clientId, hash));
        }

        return new SecuritySettings(header, keys, ReadRateLimit(configuration));
    }

    private static RateLimitSettings ReadRateLimit(IConfiguration configuration)
    {
        RateLimitSettings defaults = RateLimitSettings.Default;
        string section = $"{Root}:Api:RateLimit";
        return new RateLimitSettings(
            ReadInt(configuration, $"{section}:PerClientPermits", defaults.PerClientPermits, 1),
            TimeSpan.FromSeconds(ReadInt(configuration, $"{section}:PerClientWindowSeconds", (int)defaults.PerClientWindow.TotalSeconds, 1)),
            ReadInt(configuration, $"{section}:GlobalConcurrency", defaults.GlobalConcurrency, 1),
            ReadInt(configuration, $"{section}:GlobalQueue", defaults.GlobalQueue, 0));
    }

    private static string Required(IConfigurationSection section, string name) =>
        string.IsNullOrWhiteSpace(section[name])
            ? throw new ApiConfigurationException($"{section.Path}:{name} is required.")
            : section[name]!;

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue)
    {
        string? text = configuration[key];
        if (text is null)
        {
            return defaultValue;
        }

        return bool.TryParse(text, out bool value)
            ? value
            : throw new ApiConfigurationException($"{key} must be true or false.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int minimum)
    {
        string? text = configuration[key];
        if (text is null)
        {
            return defaultValue;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= minimum
            ? value
            : throw new ApiConfigurationException($"{key} must be a whole number of at least {minimum}.");
    }
}
