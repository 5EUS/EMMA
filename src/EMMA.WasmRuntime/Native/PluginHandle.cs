using Microsoft.Win32.SafeHandles;

namespace EMMA.WasmRuntime.Native;

/// <summary>Owns one <c>EmmaPlugin*</c>; it is closed when the last in-flight call has returned.</summary>
internal sealed class PluginHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance of the <see cref="PluginHandle"/> class.</summary>
    /// <param name="pointer">The <c>EmmaPlugin*</c> to own.</param>
    public PluginHandle(nint pointer)
        : base(ownsHandle: true)
    {
        this.SetHandle(pointer);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.PluginClose(this.handle);
        return true;
    }
}
