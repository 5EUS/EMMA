using System.Runtime.InteropServices;

namespace EMMA.Cli;

public sealed class PluginDevDoctor
{
    private const string SharedHostBridgeUrl = "http://localhost:5000";

    public IReadOnlyList<PluginDevDiagnostic> Run(PluginDevDiscoveryResult discovery, PluginDevProfile activeProfile, IReadOnlyList<PluginDevProfile> availableProfiles)
    {
        var diagnostics = new List<PluginDevDiagnostic>();

        if (string.IsNullOrWhiteSpace(discovery.ManifestPath))
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.manifest_missing",
                "No plugin manifest (*.plugin.json) was found while walking up from the current working directory.",
                PluginDevDiagnosticSeverity.Warning,
                "discovery"));
        }
        else
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.manifest_found",
                $"Discovered plugin manifest at '{discovery.ManifestPath}'.",
                PluginDevDiagnosticSeverity.Info,
                "discovery"));
        }

        if (string.IsNullOrWhiteSpace(discovery.ProjectFilePath))
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.project_missing",
                "No plugin project file (*.csproj) was found while walking up from the current working directory.",
                PluginDevDiagnosticSeverity.Warning,
                "discovery"));
        }
        else
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.project_found",
                $"Discovered plugin project at '{discovery.ProjectFilePath}'.",
                PluginDevDiagnosticSeverity.Info,
                "discovery"));
        }

        if (!string.IsNullOrWhiteSpace(discovery.PluginId)
            && !string.Equals(discovery.PluginId, activeProfile.PluginId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.profile.plugin_id_mismatch",
                $"Active profile plugin id '{activeProfile.PluginId}' differs from discovered manifest id '{discovery.PluginId}'.",
                PluginDevDiagnosticSeverity.Warning,
                "profile"));
        }

        if (discovery.SupportedTargets.Count == 0)
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.targets_unknown",
                "No supported runtime targets could be inferred from the discovered project metadata.",
                PluginDevDiagnosticSeverity.Warning,
                "discovery"));
        }
        else
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.discovery.targets_found",
                $"Discovered runtime targets: {string.Join(", ", discovery.SupportedTargets)}.",
                PluginDevDiagnosticSeverity.Info,
                "discovery"));
        }

        foreach (var target in discovery.SupportedTargets)
        {
            var artifacts = discovery.ArtifactCandidates.Where(candidate => candidate.Target == target).ToArray();
            if (artifacts.Length == 0)
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    $"doctor.artifacts.{target.ToString().ToLowerInvariant()}.unknown",
                    $"No artifact locations are defined yet for target '{target}'.",
                    PluginDevDiagnosticSeverity.Warning,
                    "artifacts"));
                continue;
            }

            if (artifacts.Any(static candidate => candidate.Exists))
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    $"doctor.artifacts.{target.ToString().ToLowerInvariant()}.ready",
                    $"Discovered existing artifacts for target '{target}'.",
                    PluginDevDiagnosticSeverity.Info,
                    "artifacts"));
            }
            else
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    $"doctor.artifacts.{target.ToString().ToLowerInvariant()}.missing",
                    $"Target '{target}' is inferred but no current artifacts were found. A build step is likely required before local execution can run.",
                    PluginDevDiagnosticSeverity.Warning,
                    "artifacts"));
            }
        }

        if (activeProfile.RuntimeTarget != PluginRuntimeTarget.Auto)
        {
            var matchingArtifacts = discovery.ArtifactCandidates.Where(candidate => candidate.Target == activeProfile.RuntimeTarget).ToArray();
            if (matchingArtifacts.Length > 0 && matchingArtifacts.All(static candidate => !candidate.Exists))
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    "doctor.profile.target_artifacts_missing",
                    $"Active profile target '{activeProfile.RuntimeTarget}' does not currently have discovered artifacts on disk.",
                    PluginDevDiagnosticSeverity.Warning,
                    "artifacts"));
            }
        }

        if (activeProfile.ExecutionMode == PluginExecutionMode.HostBridge && activeProfile.RuntimeTarget != PluginRuntimeTarget.Auto)
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.profile.host_bridge_target_metadata",
                $"Profile '{activeProfile.Name}' is currently using host-bridge execution. The inferred runtime target '{activeProfile.RuntimeTarget}' is metadata for an externally managed host or fallback workflow.",
                PluginDevDiagnosticSeverity.Info,
                "profile"));
        }

        if (activeProfile.WatchGlobs.Count == 0)
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.watch.not_configured",
                $"Profile '{activeProfile.Name}' has no watch globs configured. Phase 6 watch mode will only observe the active plugin.dev.json file when one is resolved.",
                PluginDevDiagnosticSeverity.Warning,
                "watch"));
        }
        else
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.watch.configured",
                $"Profile '{activeProfile.Name}' has {activeProfile.WatchGlobs.Count} watch glob(s) configured for debounced reload.",
                PluginDevDiagnosticSeverity.Info,
                "watch"));
        }

        if (activeProfile.Sync.Enabled)
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.sync.configured",
                $"Profile '{activeProfile.Name}' syncs build outputs to '{activeProfile.Sync.DestinationPath}' (onBuild={(activeProfile.Sync.OnBuild ? "on" : "off")}, cleanDestination={(activeProfile.Sync.CleanDestination ? "on" : "off")}).",
                PluginDevDiagnosticSeverity.Info,
                "sync"));
        }

        if (activeProfile.ExecutionMode != PluginExecutionMode.Direct)
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.watch.reload_unsupported",
                $"Profile '{activeProfile.Name}' can still observe file changes, but runtime adapter reload is not available for execution mode '{activeProfile.ExecutionMode}'.",
                PluginDevDiagnosticSeverity.Warning,
                "watch"));
        }

        if (activeProfile.ExecutionMode == PluginExecutionMode.Direct
            && activeProfile.RuntimeTarget is PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows)
        {
            if (!IsRunnableOnCurrentHost(activeProfile.RuntimeTarget))
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    "doctor.profile.native_platform_fallback_required",
                    $"Profile '{activeProfile.Name}' targets {activeProfile.RuntimeTarget}, but the current host OS cannot run that native artifact directly. Use 'host-bridge' or validate the package on a matching machine.",
                    PluginDevDiagnosticSeverity.Error,
                    "platform"));
            }
            else
            {
                diagnostics.Add(new PluginDevDiagnostic(
                    "doctor.profile.native_local_ready",
                    $"Profile '{activeProfile.Name}' can use local native process execution on this host OS when a published artifact is available.",
                    PluginDevDiagnosticSeverity.Info,
                    "platform"));
            }
        }

        if (activeProfile.ExecutionMode == PluginExecutionMode.Direct
            && activeProfile.RuntimeTarget is PluginRuntimeTarget.Linux or PluginRuntimeTarget.Windows
            && string.Equals(activeProfile.HostUrl, SharedHostBridgeUrl, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.profile.native_host_url_shared_port",
                $"Profile '{activeProfile.Name}' uses '{SharedHostBridgeUrl}', which is also the default host-bridge port. If another local service is already listening there, native direct requests can be sent to the wrong process and surface misleading unauthenticated errors.",
                PluginDevDiagnosticSeverity.Warning,
                "profile"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && availableProfiles.Any(static profile => profile.RuntimeTarget == PluginRuntimeTarget.Wasm))
        {
            diagnostics.Add(new PluginDevDiagnostic(
                "doctor.platform.macos_docker_required",
                "macOS development flows that require '-p:NativeCodeGen=llvm' should assume a Docker-backed build path unless proven otherwise.",
                PluginDevDiagnosticSeverity.Warning,
                "platform"));
        }

        return diagnostics;
    }

    private static bool IsRunnableOnCurrentHost(PluginRuntimeTarget target)
    {
        return target switch
        {
            PluginRuntimeTarget.Linux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            PluginRuntimeTarget.Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            _ => true
        };
    }

}