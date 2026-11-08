using System.Text.RegularExpressions;

namespace EMMA.PluginHost.Manifest;

/// <summary>The shared validation rules for manifest fields; they mirror <c>plugin-manifest.schema.json</c>.</summary>
internal static partial class ManifestRules
{
    /// <summary>Gets the longest relative path accepted for an entrypoint or package entry.</summary>
    public const int MaxPathLength = 260;

    /// <summary>Checks a semantic version such as <c>1.2.3</c> or <c>1.2.3-rc.1</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsSemVer(string value) => SemVer().IsMatch(value);

    /// <summary>Checks a plain <c>major.minor.patch</c> version.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsPlainVersion(string value) => PlainVersion().IsMatch(value);

    /// <summary>Checks a WIT interface id with its major version, such as <c>emma:core/catalog@1</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsInterfaceId(string value) => InterfaceId().IsMatch(value);

    /// <summary>Checks a network permission: a lower-case host name or a <c>*.example.com</c> wildcard.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsHostPattern(string value) => HostPattern().IsMatch(value);

    /// <summary>
    /// Checks a repository or key identifier. It has no path separators, so it can never
    /// name a file outside the directory it is joined to.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsIdentifier(string value) => Identifier().IsMatch(value);

    /// <summary>Checks a lower-case hex SHA-256 digest.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsSha256(string value) => Sha256().IsMatch(value);

    /// <summary>
    /// Checks that a path is a plain relative path made of safe segments: no leading slash,
    /// no backslash, no drive or stream colon, no empty, <c>.</c> or <c>..</c> segment.
    /// Segments are compared as whole segments, never as string prefixes.
    /// </summary>
    /// <param name="value">The path.</param>
    /// <returns><see langword="true"/> when the path is safe.</returns>
    public static bool IsSafeRelativePath(string value) =>
        value.Length is > 0 and <= MaxPathLength
        && SafePath().IsMatch(value)
        && Array.TrueForAll(value.Split('/'), segment => segment.Length > 0 && segment is not "." and not "..");

    /// <summary>Checks that an entrypoint path names a component file.</summary>
    /// <param name="value">The path.</param>
    /// <returns><see langword="true"/> when the path is safe and ends in <c>.wasm</c>. Precompiled <c>.cwasm</c> files are machine code the signature cannot vouch for, so the host makes its own and packages never carry one.</returns>
    public static bool IsEntrypointPath(string value) =>
        IsSafeRelativePath(value) && value.EndsWith(".wasm", StringComparison.Ordinal);

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex SemVer();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex PlainVersion();

    [GeneratedRegex("^[a-z][a-z0-9-]*:[a-z][a-z0-9-]*/[a-z][a-z0-9-]*@\\d+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex InterfaceId();

    [GeneratedRegex("^(\\*\\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Identifier();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex Sha256();

    [GeneratedRegex("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex SafePath();
}
