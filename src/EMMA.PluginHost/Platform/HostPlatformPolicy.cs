using EMMA.PluginHost.Configuration;

namespace EMMA.PluginHost.Platform;

public enum HostPlatform
{
    Unknown = 0,
    Android,
    AppleMobile,
    Windows,
    Linux,
    MacOS
}

public static class HostPlatformPolicy
{
    public static HostPlatform Current => DetectCurrent();

    public static bool UsesInternalNativeWasmLibrary(PluginHostOptions options)
    {
        return options.NativeWasmLibraryMode switch
        {
            NativeWasmLibraryMode.Internal => true,
            NativeWasmLibraryMode.External => false,
            _ => Current == HostPlatform.AppleMobile
        };
    }

    public static bool AllowsProcessPlugins(PluginHostOptions options)
    {
        if (options.EnableProcessPlugins.HasValue)
        {
            return options.EnableProcessPlugins.Value;
        }

        return Current is not HostPlatform.AppleMobile and not HostPlatform.MacOS;
    }

    public static bool AllowsWasmPlugins(PluginHostOptions options)
    {
        if (options.EnableWasmPlugins.HasValue)
        {
            return options.EnableWasmPlugins.Value;
        }

        return true;
    }

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
