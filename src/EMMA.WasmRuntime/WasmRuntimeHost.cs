using System.Text;
using System.Text.Json;

using EMMA.Domain;
using EMMA.WasmRuntime.Native;
using EMMA.WasmRuntime.Wire;

namespace EMMA.WasmRuntime;

/// <summary>
/// The native Wasm host: one engine shared by every plugin opened from it. Create one per process and
/// dispose it at shutdown; plugins opened from it keep working until they are disposed themselves.
/// </summary>
public sealed class WasmRuntimeHost : IDisposable
{
    private readonly RuntimeHandle handle;

    private WasmRuntimeHost(nint pointer) => this.handle = new RuntimeHandle(pointer);

    /// <summary>
    /// Adds a directory to search for the native Wasm host, ahead of the application directory. A library loaded into
    /// someone else's process (an app embedding EMMA) is not found next to that process, so its owner says where it is.
    /// </summary>
    /// <param name="directory">The directory holding <c>libemma_wasm</c>.</param>
    public static void AddLibrarySearchDirectory(string directory) => NativeLibraryResolver.AddDirectory(directory);

    /// <summary>Creates the runtime.</summary>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The runtime.</returns>
    /// <exception cref="WasmRuntimeException">The native library is missing or refused to start.</exception>
    public static WasmRuntimeHost Create(WasmRuntimeOptions? options = null)
    {
        NativeLibraryResolver.EnsureRegistered();
        byte[] json = options?.Target is null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(new RuntimeOptionsDocument(options.Target), ConfigJsonContext.Default.RuntimeOptionsDocument);

        int code;
        nint runtime;
        EmmaBuffer error;
        try
        {
            code = NativeMethods.RuntimeCreate(json, (nuint)json.Length, out runtime, out error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new WasmRuntimeException(
                $"The native Wasm host (libemma_wasm) could not be loaded. Build native/emma-wasm and place the library next to the application, or set {NativeLibraryResolver.OverrideVariable}.",
                ex);
        }

        if (code != NativeMethods.Ok)
        {
            throw new WasmRuntimeException(Message(error.Take(), "The Wasm runtime could not be created."));
        }

        error.Take();
        return new WasmRuntimeHost(runtime);
    }

    /// <summary>Gets the key a precompiled component must have been built with to load on this host.</summary>
    /// <returns>The key, as text.</returns>
    public string Key()
    {
        int code = NativeMethods.RuntimeKey(this.handle, out EmmaBuffer key);
        byte[] bytes = key.Take();
        return code == NativeMethods.Ok
            ? Encoding.UTF8.GetString(bytes)
            : throw new WasmRuntimeException("The Wasm runtime did not report its compatibility key.");
    }

    /// <summary>Compiles a component ahead of time for this host, so that opening it later is fast.</summary>
    /// <param name="component">The component bytes.</param>
    /// <returns>The precompiled bytes, or why the component was refused.</returns>
    public Outcome<byte[]> Precompile(ReadOnlyMemory<byte> component)
    {
        int code = NativeMethods.Precompile(this.handle, component.Span, (nuint)component.Length, out EmmaBuffer cwasm, out EmmaBuffer error);
        byte[] output = cwasm.Take();
        string message = Message(error.Take(), "The component could not be precompiled.");
        return code == NativeMethods.Ok
            ? Outcome.Success(output)
            : Outcome.Failure<byte[]>(FailureCode(code), message);
    }

    /// <summary>Opens a plugin: loads and links the component, checks its exports and applies the limits.</summary>
    /// <param name="config">What to open and how.</param>
    /// <returns>The plugin, or why it could not be opened.</returns>
    public Outcome<WasmPlugin> Open(WasmPluginConfig config)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(ToDocument(config), ConfigJsonContext.Default.ConfigDocument);
        int code = NativeMethods.PluginOpen(this.handle, json, (nuint)json.Length, out nint plugin, out EmmaBuffer info, out EmmaBuffer error);
        byte[] infoBytes = info.Take();
        string message = Message(error.Take(), "The plugin could not be opened.");
        if (code != NativeMethods.Ok)
        {
            return Outcome.Failure<WasmPlugin>(FailureCode(code), message);
        }

        WireOpenInfo? parsed = JsonSerializer.Deserialize(infoBytes, WireJsonContext.Default.WireOpenInfo);
        if (parsed is null)
        {
            NativeMethods.PluginClose(plugin);
            return Outcome.Failure<WasmPlugin>(ErrorCode.Internal, "The Wasm host did not describe the opened plugin.");
        }

        // Ownership of the plugin passes to the caller inside the Outcome, which the disposal analyzers cannot follow.
#pragma warning disable CA2000, IDISP001
        WasmPlugin opened = new(plugin, parsed.SearchAssist);
#pragma warning restore CA2000, IDISP001
        return Outcome.Success(opened);
    }

    /// <inheritdoc/>
    public void Dispose() => this.handle.Dispose();

    private static ErrorCode FailureCode(int code) =>
        code == NativeMethods.ErrorArgument ? ErrorCode.InvalidArgument : ErrorCode.Internal;

    private static string Message(byte[] bytes, string fallback) =>
        bytes.Length == 0 ? fallback : Encoding.UTF8.GetString(bytes);

    private static ConfigDocument ToDocument(WasmPluginConfig config) => new(
        config.ComponentPath,
        config.Precompiled,
        new ConfigLimits(config.Limits.CpuBudgetMs, config.Limits.MemoryMb, config.Limits.CallTimeoutMs),
        new ConfigPermissions(config.Network, config.Cache),
        config.Settings,
        config.Testing is null ? null : new ConfigTesting(config.Testing.AllowPrivateAddresses, config.Testing.FixedNowMs));
}
