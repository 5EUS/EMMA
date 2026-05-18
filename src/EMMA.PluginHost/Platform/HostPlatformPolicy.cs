using EMMA.PluginHost.Configuration;

namespace EMMA.PluginHost.Platform;

/// <summary>
/// Represents the host operating system family used to apply plugin runtime policy.
/// </summary>
public enum HostPlatform
{
    /// <summary>
    /// The host platform could not be identified.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// An Android-based host.
    /// </summary>
    Android,

    /// <summary>
    /// An Apple mobile host such as iOS, tvOS, or Mac Catalyst.
    /// </summary>
    AppleMobile,

    /// <summary>
    /// A Windows host.
    /// </summary>
    Windows,

    /// <summary>
    /// A Linux host.
    /// </summary>
    Linux,

    /// <summary>
    /// A macOS desktop host.
    /// </summary>
    MacOS
}

/// <summary>
/// Applies platform-specific defaults and capability checks for plugin hosting.
/// </summary>
public static class HostPlatformPolicy
{
    /// <summary>
    /// Gets the current host platform classification.
    /// </summary>
    public static HostPlatform Current => DetectCurrent();

    /// <summary>
    /// Determines whether the host should use the internally managed native WASM helper library.
    /// </summary>
    /// <param name="options">The plugin host configuration.</param>
    /// <returns><see langword="true"/> when the internal native library should be used; otherwise, <see langword="false"/>.</returns>
    public static bool UsesInternalNativeWasmLibrary(PluginHostOptions options)
    {
        return options.NativeWasmLibraryMode switch
        {
            NativeWasmLibraryMode.Internal => true,
            NativeWasmLibraryMode.External => false,
            _ => Current == HostPlatform.AppleMobile
        };
    }

    /// <summary>
    /// Determines whether process-based plugins are allowed on the current platform.
    /// </summary>
    /// <param name="options">The plugin host configuration.</param>
    /// <returns><see langword="true"/> when process plugins are enabled for the host; otherwise, <see langword="false"/>.</returns>
    public static bool AllowsProcessPlugins(PluginHostOptions options)
    {
        if (options.EnableProcessPlugins.HasValue)
        {
            return options.EnableProcessPlugins.Value;
        }

        return Current is not HostPlatform.AppleMobile and not HostPlatform.MacOS;
    }

    /// <summary>
    /// Determines whether WASM plugins are allowed on the current platform.
    /// </summary>
    /// <param name="options">The plugin host configuration.</param>
    /// <returns><see langword="true"/> when WASM plugins are enabled for the host; otherwise, <see langword="false"/>.</returns>
    public static bool AllowsWasmPlugins(PluginHostOptions options)
    {
        if (options.EnableWasmPlugins.HasValue)
        {
            return options.EnableWasmPlugins.Value;
        }

        return true;
    }

    /// <summary>
    /// Determines whether externally hosted endpoint plugins are allowed on the current platform.
    /// </summary>
    /// <param name="options">The plugin host configuration.</param>
    /// <returns><see langword="true"/> when external endpoint plugins are enabled for the host; otherwise, <see langword="false"/>.</returns>
    public static bool AllowsExternalEndpointPlugins(PluginHostOptions options)
    {
        if (options.EnableExternalEndpointPlugins.HasValue)
        {
            return options.EnableExternalEndpointPlugins.Value;
        }

        return true;
    }

    private static HostPlatform DetectCurrent()
    {
        if (OperatingSystem.IsAndroid())
        {
            return HostPlatform.Android;
        }

        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
        {
            return HostPlatform.AppleMobile;
        }

        if (OperatingSystem.IsWindows())
        {
            return HostPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return HostPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return HostPlatform.MacOS;
        }

        return HostPlatform.Unknown;
    }
}
