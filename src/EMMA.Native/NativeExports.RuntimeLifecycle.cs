using System.Runtime.InteropServices;

using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

namespace EMMA.Native;

public static partial class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        EnsureNativeLoggingConfigured();
        ClearLastError();
        LogInfo("runtime", "RuntimeStart requested.");

        try
        {
            EnsurePluginHostInitialized();

            lock (RuntimeLifecycleLock)
            {
                if (_sharedRuntimeHandle is { } existingHandle
                    && States.ContainsKey(existingHandle))
                {
                    _runtimeReferenceCount++;
                    LogDebug("runtime", $"Runtime reused. handle={existingHandle}, refCount={_runtimeReferenceCount}");
                    return existingHandle;
                }

                var store = new InMemoryMediaStore();
                IMediaSearchPort search = new InMemorySearchPort(store);
                IPageProviderPort pages = new InMemoryPageProvider(store);
                IPolicyEvaluator policy = new HostPolicyEvaluator();

                var runtime = EmbeddedRuntimeFactory.Create(search, pages, policy);

                var handle = Interlocked.Increment(ref _nextHandle);
                States[handle] = new RuntimeState(runtime, store);
                _sharedRuntimeHandle = handle;
                _runtimeReferenceCount = 1;
                LogInfo("runtime", $"Runtime started. handle={handle}");
                return handle;
            }
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_stop")]
    public static void RuntimeStop(int handle)
    {
        ClearLastError();
        LogInfo("runtime", $"RuntimeStop requested. handle={handle}");

        try
        {
            var shouldShutdownPluginHost = false;

            lock (RuntimeLifecycleLock)
            {
                if (!States.ContainsKey(handle))
                {
                    LogDebug("runtime", $"RuntimeStop ignored for missing handle={handle}");
                    return;
                }

                if (_sharedRuntimeHandle == handle)
                {
                    if (_runtimeReferenceCount > 0)
                    {
                        _runtimeReferenceCount--;
                    }

                    if (_runtimeReferenceCount > 0)
                    {
                        LogDebug("runtime", $"Runtime kept alive. handle={handle}, refCount={_runtimeReferenceCount}");
                        return;
                    }

                    _sharedRuntimeHandle = null;
                    _runtimeReferenceCount = 0;
                }

                States.TryRemove(handle, out _);
                shouldShutdownPluginHost = States.IsEmpty;
            }

            if (shouldShutdownPluginHost)
            {
                ShutdownPluginHost();
            }

            LogInfo("runtime", $"Runtime stopped. handle={handle}");
        }
        catch (Exception ex)
        {
            SetLastError(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_status")]
    public static int RuntimeStatus(int handle)
    {
        return States.ContainsKey(handle) ? 1 : 0;
    }
}