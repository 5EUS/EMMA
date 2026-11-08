using System.Runtime.InteropServices;

namespace EMMA.WasmRuntime.Native;

/// <summary>The C ABI of libemma_wasm, mirrored from <c>native/emma-wasm/include/emma_wasm.h</c>.</summary>
internal static partial class NativeMethods
{
    /// <summary>The logical library name resolved by <see cref="NativeLibraryResolver"/>.</summary>
    internal const string Library = "emma_wasm";

    /// <summary>Success.</summary>
    internal const int Ok = 0;

    /// <summary>The host refused an argument.</summary>
    internal const int ErrorArgument = 1;

    /// <summary>The host failed to do what was asked.</summary>
    internal const int ErrorRuntime = 2;

    /// <summary>The host caught an internal panic.</summary>
    internal const int ErrorPanic = 3;

    /// <summary>Frees a buffer the host returned.</summary>
    /// <param name="buffer">The buffer.</param>
    [LibraryImport(Library, EntryPoint = "emma_buffer_free")]
    internal static partial void BufferFree(EmmaBuffer buffer);

    /// <summary>Creates a runtime.</summary>
    /// <param name="options">Optional JSON such as the compile target; may be empty.</param>
    /// <param name="optionsLength">The length of <paramref name="options"/>.</param>
    /// <param name="runtime">Receives the runtime pointer.</param>
    /// <param name="error">Receives a message on failure.</param>
    /// <returns>A status code.</returns>
    [LibraryImport(Library, EntryPoint = "emma_runtime_create")]
    internal static partial int RuntimeCreate(
        ReadOnlySpan<byte> options,
        nuint optionsLength,
        out nint runtime,
        out EmmaBuffer error);

    /// <summary>Destroys a runtime. Plugins opened from it keep working until closed.</summary>
    /// <param name="runtime">The runtime pointer.</param>
    [LibraryImport(Library, EntryPoint = "emma_runtime_destroy")]
    internal static partial void RuntimeDestroy(nint runtime);

    /// <summary>Gets the key a precompiled component must have been built with to load here.</summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="key">Receives the key.</param>
    /// <returns>A status code.</returns>
    [LibraryImport(Library, EntryPoint = "emma_runtime_key")]
    internal static partial int RuntimeKey(RuntimeHandle runtime, out EmmaBuffer key);

    /// <summary>Compiles a component ahead of time for this host.</summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="wasm">The component bytes.</param>
    /// <param name="wasmLength">The length of <paramref name="wasm"/>.</param>
    /// <param name="cwasm">Receives the precompiled bytes.</param>
    /// <param name="error">Receives a message on failure.</param>
    /// <returns>A status code.</returns>
    [LibraryImport(Library, EntryPoint = "emma_precompile")]
    internal static partial int Precompile(
        RuntimeHandle runtime,
        ReadOnlySpan<byte> wasm,
        nuint wasmLength,
        out EmmaBuffer cwasm,
        out EmmaBuffer error);

    /// <summary>Opens a plugin from a JSON configuration.</summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="config">The JSON <c>PluginConfig</c>.</param>
    /// <param name="configLength">The length of <paramref name="config"/>.</param>
    /// <param name="plugin">Receives the plugin pointer.</param>
    /// <param name="info">Receives which optional interfaces the plugin exports.</param>
    /// <param name="error">Receives a message on failure.</param>
    /// <returns>A status code.</returns>
    [LibraryImport(Library, EntryPoint = "emma_plugin_open")]
    internal static partial int PluginOpen(
        RuntimeHandle runtime,
        ReadOnlySpan<byte> config,
        nuint configLength,
        out nint plugin,
        out EmmaBuffer info,
        out EmmaBuffer error);

    /// <summary>Closes a plugin.</summary>
    /// <param name="plugin">The plugin pointer.</param>
    [LibraryImport(Library, EntryPoint = "emma_plugin_close")]
    internal static partial void PluginClose(nint plugin);

    /// <summary>Runs one operation; plugin failures and traps are inside the JSON envelope.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="operationLength">The length of <paramref name="operation"/>.</param>
    /// <param name="request">The JSON request.</param>
    /// <param name="requestLength">The length of <paramref name="request"/>.</param>
    /// <param name="json">Receives the JSON envelope.</param>
    /// <param name="blob">Receives the bytes of a <c>read</c>.</param>
    /// <param name="error">Receives a message on failure.</param>
    /// <returns>A status code.</returns>
    [LibraryImport(Library, EntryPoint = "emma_plugin_call")]
    internal static partial int PluginCall(
        PluginHandle plugin,
        ReadOnlySpan<byte> operation,
        nuint operationLength,
        ReadOnlySpan<byte> request,
        nuint requestLength,
        out EmmaBuffer json,
        out EmmaBuffer blob,
        out EmmaBuffer error);
}
