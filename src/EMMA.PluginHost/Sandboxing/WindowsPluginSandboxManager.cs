using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Sandboxing;

/// <summary>
/// Windows sandbox scaffolding placeholder (job objects, low-integrity tokens).
/// </summary>
public sealed class WindowsPluginSandboxManager(IOptions<PluginHostOptions> options, ILogger<WindowsPluginSandboxManager> logger)
    : PluginSandboxManagerBase(options, logger)
{
    protected override string PlatformName => "Windows";

    private readonly ConcurrentDictionary<string, SafeJobHandle> _jobs = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<bool> PrepareSandboxAsync(
        PluginManifest manifest,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public override Task EnforceAsync(PluginManifest manifest, Process process, CancellationToken cancellationToken)
    {
        if (!Options.SandboxEnabled)
        {
            return Task.CompletedTask;
        }

        if (process.HasExited)
        {
            return Task.CompletedTask;
        }

        // TODO Job objects provide lifecycle isolation and kill-on-close, not a full sandbox.
        var handle = _jobs.GetOrAdd(manifest.Id, _ => CreateJobHandle(manifest.Id));

        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("Failed to assign plugin {PluginId} to Windows job object.", manifest.Id);
            }
        }

        return Task.CompletedTask;
    }

    private static SafeJobHandle CreateJobHandle(string name)
    {
        var handle = CreateJobObject(IntPtr.Zero, name);
        if (handle.IsInvalid)
        {
            return handle;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitFlags.KillOnJobClose | JobObjectLimitFlags.ActiveProcess,
                ActiveProcessLimit = 1
            }
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var result = SetInformationJobObject(
            handle,
            JobObjectInfoType.ExtendedLimitInformation,
            ref limits,
            (uint)length);

        if (!result)
        {
            handle.Dispose();
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        ActiveProcess = 0x00000008,
        KillOnJobClose = 0x00002000
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInfoType infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInfo,
        uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
