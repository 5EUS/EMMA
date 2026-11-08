using Microsoft.Win32.SafeHandles;

namespace EMMA.WasmRuntime.Native;

/// <summary>Owns one <c>EmmaRuntime*</c>; the native runtime is destroyed when the last user lets go.</summary>
internal sealed class RuntimeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance of the <see cref="RuntimeHandle"/> class.</summary>
    /// <param name="pointer">The <c>EmmaRuntime*</c> to own.</param>
    public RuntimeHandle(nint pointer)
        : base(ownsHandle: true)
    {
        this.SetHandle(pointer);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeMethods.RuntimeDestroy(this.handle);
        return true;
    }
}
