using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Platform;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

public sealed class PluginRepositoryInstallOrchestrator(
    PluginRepositoryService repositoryService,
    PluginRepositoryCatalogClient catalogClient,
    PluginProcessManager processManager,
    PluginHandshakeService handshakeService,
    IPluginSignatureVerifier signatureVerifier,
    IOptions<PluginSignatureOptions> signatureOptions,
    IOptions<PluginHostOptions> hostOptions,
    ILogger<PluginRepositoryInstallOrchestrator> logger)
{
    private static readonly IReadOnlyDictionary<HostPlatform, HashSet<string>> SupportedPayloadTypesByPlatform =
        new Dictionary<HostPlatform, HashSet<string>>
        {
            [HostPlatform.AppleMobile] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wasm", "cwasm" },
            [HostPlatform.Android] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wasm", "cwasm" },
            [HostPlatform.MacOS] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wasm", "cwasm" },
            [HostPlatform.Windows] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wasm", "cwasm" },
            [HostPlatform.Linux] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wasm", "cwasm", "native-linux-bundle" }
        };
    private static readonly HashSet<string> PayloadPlatformTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "wasm",
        "cwasm"
    };

    private readonly PluginRepositoryService _repositoryService = repositoryService;
    private readonly PluginRepositoryCatalogClient _catalogClient = catalogClient;
    private readonly PluginProcessManager _processManager = processManager;
    private readonly PluginHandshakeService _handshakeService = handshakeService;
    private readonly IPluginSignatureVerifier _signatureVerifier = signatureVerifier;
    private readonly PluginSignatureOptions _signatureOptions = signatureOptions.Value;
    private readonly PluginHostOptions _hostOptions = hostOptions.Value;
    private readonly ILogger<PluginRepositoryInstallOrchestrator> _logger = logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public async Task<PluginRepositoryInstallResult> InstallFromRepositoryAsync(
        InstallPluginFromRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepositoryId))
        {
            throw new ArgumentException("RepositoryId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new ArgumentException("PluginId is required.", nameof(request));
        }

        var selection = await _repositoryService.ResolvePluginReleaseAsync(
            request.RepositoryId,
            request.PluginId,
            request.Version,
            request.RefreshCatalog,
            cancellationToken);

        ValidateReleasePlatform(selection.Release);

        var releaseHash = NormalizeSha256(selection.Release.Sha256)
            ?? throw new InvalidDataException(
                $"Release '{selection.Release.Version}' for plugin '{selection.Plugin.PluginId}' has an invalid sha256 value.");

        var stagingRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-repository", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        var installStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var (archivePath, downloadedHash, _) = await _catalogClient.DownloadArtifactAsync(
                request.RepositoryId,
                selection.Release.AssetUrl,
                stagingRoot,
                cancellationToken);

            if (!string.Equals(downloadedHash, releaseHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Release hash mismatch for plugin '{selection.Plugin.PluginId}'. Expected {releaseHash}, got {downloadedHash}.");
            }

            var parsed = await ParseAndValidateArchiveAsync(
                archivePath,
                selection.Plugin.PluginId,
                selection.Release,
                cancellationToken);

            await _installGate.WaitAsync(cancellationToken);
            try
            {
                await CommitInstallAsync(parsed, cancellationToken);
            }
            finally
            {
                _installGate.Release();
            }

            if (request.RescanAfterInstall)
            {
                await _handshakeService.RescanAsync(cancellationToken);
            }

            return new PluginRepositoryInstallResult(
                Success: true,
                RepositoryId: request.RepositoryId,
                PluginId: parsed.Manifest.Id,
                Version: parsed.Manifest.Version,
                InstalledManifestPath: parsed.ManifestDestination,
                InstalledPluginPath: parsed.PluginDestination,
                PayloadType: parsed.PayloadType,
                InstalledAtUtc: DateTimeOffset.UtcNow,
                Message: "Plugin installed successfully from repository.");
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Repository install failed for {RepositoryId}/{PluginId} (requested version: {Version}).",
                    request.RepositoryId,
                    request.PluginId,
                    request.Version ?? "latest");
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Repository install flow completed for {RepositoryId}/{PluginId} in {ElapsedMs}ms",
                    request.RepositoryId,
                    request.PluginId,
                    (DateTimeOffset.UtcNow - installStartedAt).TotalMilliseconds);
            }
        }
    }

    private async Task<ParsedInstallPayload> ParseAndValidateArchiveAsync(
        string archivePath,
        string expectedPluginId,
        PluginRepositoryCatalogRelease expectedRelease,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var manifestEntries = archive.Entries
            .Where(entry =>
                !string.IsNullOrEmpty(entry.Name)
                && entry.FullName.StartsWith("manifest/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (manifestEntries.Count != 1)
        {
            throw new InvalidDataException("Invalid plugin package: expected exactly one manifest/*.json file.");
        }

        var manifestText = await ReadEntryTextAsync(manifestEntries[0], cancellationToken);
        var manifest = JsonSerializer.Deserialize(manifestText, PluginManifestJsonContext.Default.PluginManifest)
            ?? throw new InvalidDataException("Invalid plugin package: manifest could not be parsed.");

        if (!string.Equals(manifest.Id, expectedPluginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Plugin id mismatch. Expected '{expectedPluginId}', archive manifest reports '{manifest.Id}'.");
        }

        if (!IsReleaseVersionCompatible(expectedRelease.Version, expectedRelease.Platforms, manifest.Version))
        {
            throw new InvalidDataException(
            $"Plugin version mismatch. Expected '{expectedRelease.Version}', archive manifest reports '{manifest.Version}'.");
        }

        ValidateSignaturePolicy(manifest);

        var pluginPrefix = manifest.Id + "/";
        var pluginEntries = archive.Entries
            .Where(entry =>
                entry.FullName.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pluginEntries.Count == 0)
        {
            throw new InvalidDataException(
                $"Invalid plugin package: expected plugin payload under '{pluginPrefix}'.");
        }

        var payloadType = DetectPayloadType(pluginEntries);
        EnsurePayloadSupportedForCurrentPlatform(payloadType);

        var installStagingRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-stage", Guid.NewGuid().ToString("N"));
        var stagedPluginRoot = Path.Combine(installStagingRoot, manifest.Id);
        Directory.CreateDirectory(stagedPluginRoot);

        foreach (var entry in pluginEntries)
        {
            var relative = entry.FullName;
            if (relative.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (relative.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[pluginPrefix.Length..];
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(stagedPluginRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(stagedPluginRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid plugin package: unsafe path found in payload.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                ApplyUnixPermissions(entry, destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var sourceStream = entry.Open();
            await using var fileStream = File.Create(destination);
            await sourceStream.CopyToAsync(fileStream, cancellationToken);
            ApplyUnixPermissions(entry, destination);
        }

        var stagedManifestPath = Path.Combine(installStagingRoot, $"{manifest.Id}.plugin.json");
        await File.WriteAllTextAsync(stagedManifestPath, manifestText, Encoding.UTF8, cancellationToken);

        var manifestDestination = Path.Combine(Path.GetFullPath(_hostOptions.ManifestDirectory), $"{manifest.Id}.plugin.json");
        var pluginDestination = Path.Combine(Path.GetFullPath(_hostOptions.SandboxRootDirectory), manifest.Id);

        return new ParsedInstallPayload(
            Manifest: manifest,
            PayloadType: payloadType,
            StagingRoot: installStagingRoot,
            StagedManifestPath: stagedManifestPath,
            StagedPluginPath: stagedPluginRoot,
            ManifestDestination: manifestDestination,
            PluginDestination: pluginDestination);
    }

    private async Task CommitInstallAsync(ParsedInstallPayload payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(payload.ManifestDestination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(payload.PluginDestination)!);

        var backupRoot = Path.Combine(Path.GetTempPath(), "emma-plugin-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);

        var backupPluginPath = Path.Combine(backupRoot, payload.Manifest.Id);
        var backupManifestPath = Path.Combine(backupRoot, $"{payload.Manifest.Id}.plugin.json");

        var pluginMovedToBackup = false;
        var manifestMovedToBackup = false;

        try
        {
            await _processManager.StopAsync(payload.Manifest.Id, cancellationToken);

            if (Directory.Exists(payload.PluginDestination))
            {
                MoveDirectoryWithFallback(payload.PluginDestination, backupPluginPath);
                pluginMovedToBackup = true;
            }

            if (File.Exists(payload.ManifestDestination))
            {
                MoveFileWithFallback(payload.ManifestDestination, backupManifestPath);
                manifestMovedToBackup = true;
            }

            MoveDirectoryWithFallback(payload.StagedPluginPath, payload.PluginDestination);
            MoveFileWithFallback(payload.StagedManifestPath, payload.ManifestDestination);

            // Preserve runtime cache data (for example, thumbnails) across plugin updates.
            RestorePluginCacheFromBackup(backupPluginPath, payload.PluginDestination);

            TryDeleteDirectory(backupRoot);
            TryDeleteDirectory(payload.StagingRoot);
        }
        catch
        {
            TryDeleteDirectory(payload.PluginDestination);
            TryDeleteFile(payload.ManifestDestination);

            if (pluginMovedToBackup && Directory.Exists(backupPluginPath))
            {
                MoveDirectoryWithFallback(backupPluginPath, payload.PluginDestination);
            }

            if (manifestMovedToBackup && File.Exists(backupManifestPath))
            {
                MoveFileWithFallback(backupManifestPath, payload.ManifestDestination);
            }

            TryDeleteDirectory(backupRoot);
            TryDeleteDirectory(payload.StagingRoot);
            throw;
        }
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private void ValidateSignaturePolicy(PluginManifest manifest)
    {
        if (!_signatureOptions.RequireSignedPlugins)
        {
            return;
        }

        if (manifest.Signature is null)
        {
            throw new InvalidDataException("Plugin manifest signature is required for installation.");
        }

        if (!_signatureVerifier.Verify(manifest, out var reason))
        {
            throw new InvalidDataException(reason ?? "Plugin manifest signature validation failed.");
        }
    }

    private void ValidateReleasePlatform(PluginRepositoryCatalogRelease release)
    {
        var platformTags = release.Platforms
            ?.Select(item => item.Trim().ToLowerInvariant())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (platformTags is null || platformTags.Count == 0)
        {
            return;
        }

        var current = GetCurrentPlatformKey();
        if (platformTags.Contains(current))
        {
            return;
        }

        if (platformTags.Contains("all") || platformTags.Contains("any") || platformTags.Contains("*"))
        {
            return;
        }

        var payloadTags = platformTags
            .Where(PayloadPlatformTags.Contains)
            .ToList();

        if (payloadTags.Count > 0 && payloadTags.Any(IsPayloadSupportedOnCurrentPlatform))
        {
            return;
        }

        if (!platformTags.Contains(current))
        {
            throw new InvalidDataException(
                $"Release '{release.Version}' does not target platform '{current}'.");
        }
    }

    private bool IsPayloadSupportedOnCurrentPlatform(string payloadType)
    {
        var platform = HostPlatformPolicy.Current;
        if (!SupportedPayloadTypesByPlatform.TryGetValue(platform, out var allowed))
        {
            return false;
        }

        return allowed.Contains(payloadType);
    }

    private void EnsurePayloadSupportedForCurrentPlatform(string payloadType)
    {
        var platform = HostPlatformPolicy.Current;
        if (!SupportedPayloadTypesByPlatform.TryGetValue(platform, out var allowed))
        {
            throw new InvalidDataException($"Current platform '{platform}' is not supported for repository plugin installation.");
        }

        if (!allowed.Contains(payloadType))
        {
            throw new InvalidDataException(
                $"Plugin payload type '{payloadType}' is not supported on platform '{GetCurrentPlatformKey()}'.");
        }
    }

    private static string DetectPayloadType(IReadOnlyList<ZipArchiveEntry> entries)
    {
        var names = entries
            .Select(entry => entry.FullName.Replace('\\', '/').ToLowerInvariant())
            .ToList();

        if (names.Any(name => name.EndsWith("/plugin.cwasm", StringComparison.Ordinal)))
        {
            return "cwasm";
        }

        if (names.Any(name => name.EndsWith("/plugin.wasm", StringComparison.Ordinal)))
        {
            return "wasm";
        }

        if (names.Any(name => name.Contains(".app/", StringComparison.Ordinal)))
        {
            return "native-macos-app";
        }

        if (names.Any(name => name.Contains("/windows/", StringComparison.Ordinal) || name.EndsWith(".exe", StringComparison.Ordinal)))
        {
            return "native-windows-bundle";
        }

        if (names.Any(name =>
                name.Contains("/linux/", StringComparison.Ordinal)
                || name.EndsWith(".so", StringComparison.Ordinal)
            || name.EndsWith("/createdump", StringComparison.Ordinal)
            || IsLikelyLinuxNativeExecutablePath(name)))
        {
            return "native-linux-bundle";
        }

        return "unknown";
    }

    private static bool IsLikelyLinuxNativeExecutablePath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || normalizedPath.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(fileName, "readme", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "license", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "notice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static void ApplyUnixPermissions(ZipArchiveEntry entry, string destinationPath)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        var permissions = (entry.ExternalAttributes >> 16) & 0x1FF;
        if (permissions <= 0)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destinationPath, (UnixFileMode)permissions);
        }
        catch
        {
            // Intentionally ignored: permission restoration is best effort.
        }
    }

    private static string? NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal))
        {
            normalized = normalized["sha256:".Length..];
        }

        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsReleaseVersionCompatible(
        string releaseVersion,
        IReadOnlyList<string>? releasePlatforms,
        string manifestVersion)
    {
        if (string.Equals(releaseVersion, manifestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRelease = NormalizeVersionWithPlatformSuffix(releaseVersion, releasePlatforms);
        if (normalizedRelease is null)
        {
            return false;
        }

        return string.Equals(normalizedRelease, manifestVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeVersionWithPlatformSuffix(string releaseVersion, IReadOnlyList<string>? releasePlatforms)
    {
        if (string.IsNullOrWhiteSpace(releaseVersion) || releasePlatforms is null || releasePlatforms.Count == 0)
        {
            return null;
        }

        var normalizedVersion = releaseVersion.Trim();
        foreach (var rawPlatform in releasePlatforms)
        {
            var platform = rawPlatform?.Trim();
            if (string.IsNullOrWhiteSpace(platform))
            {
                continue;
            }

            var suffix = "-" + platform.ToLowerInvariant();
            if (normalizedVersion.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && normalizedVersion.Length > suffix.Length)
            {
                return normalizedVersion[..^suffix.Length];
            }
        }

        return null;
    }

    private static string GetCurrentPlatformKey()
    {
        return HostPlatformPolicy.Current switch
        {
            HostPlatform.Android => "android",
            HostPlatform.AppleMobile => "ios",
            HostPlatform.Windows => "windows",
            HostPlatform.Linux => "linux",
            HostPlatform.MacOS => "macos",
            _ => "unknown"
        };
    }

    private static void MoveDirectoryWithFallback(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.Move(sourcePath, destinationPath);
        }
        catch (IOException) when (Directory.Exists(sourcePath) && !Directory.Exists(destinationPath))
        {
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }
    }

    private static void MoveFileWithFallback(string sourcePath, string destinationPath)
    {
        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch (IOException) when (File.Exists(sourcePath) && !File.Exists(destinationPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            File.Delete(sourcePath);
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            var targetDirectory = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(targetDirectory);
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var targetFile = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static void RestorePluginCacheFromBackup(string backupPluginPath, string pluginDestination)
    {
        if (string.IsNullOrWhiteSpace(backupPluginPath)
            || string.IsNullOrWhiteSpace(pluginDestination)
            || !Directory.Exists(backupPluginPath)
            || !Directory.Exists(pluginDestination))
        {
            return;
        }

        var backupCachePath = Path.Combine(backupPluginPath, "cache");
        if (!Directory.Exists(backupCachePath))
        {
            return;
        }

        var destinationCachePath = Path.Combine(pluginDestination, "cache");
        CopyDirectoryContentsNoOverwrite(backupCachePath, destinationCachePath);
    }

    private static void CopyDirectoryContentsNoOverwrite(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            var targetDirectory = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(targetDirectory);
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var targetFile = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            if (File.Exists(targetFile))
            {
                continue;
            }

            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignored
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    private sealed record ParsedInstallPayload(
        PluginManifest Manifest,
        string PayloadType,
        string StagingRoot,
        string StagedManifestPath,
        string StagedPluginPath,
        string ManifestDestination,
        string PluginDestination);
}
