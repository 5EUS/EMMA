using System.Runtime.InteropServices;

namespace EMMA.WasmRuntime.Native;

/// <summary>An owned byte buffer allocated by the native host (<c>EmmaBuffer</c> in emma_wasm.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EmmaBuffer
{
    /// <summary>The first byte, or zero for an empty buffer.</summary>
    public nint Pointer;

    /// <summary>The length in bytes.</summary>
    public nuint Length;

    /// <summary>The allocation capacity; opaque to this side, needed to free the buffer.</summary>
    public nuint Capacity;

    /// <summary>Copies the bytes out and frees the native buffer. Safe on an empty buffer.</summary>
    /// <returns>The bytes.</returns>
    public byte[] Take()
    {
        byte[] bytes = [];
        if (this.Pointer != 0 && this.Length > 0)
        {
            bytes = new byte[checked((int)this.Length)];
            Marshal.Copy(this.Pointer, bytes, 0, bytes.Length);
        }

        NativeMethods.BufferFree(this);
        this = default;
        return bytes;
    }
}
