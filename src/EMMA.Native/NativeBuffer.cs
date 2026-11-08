using System.Runtime.InteropServices;

namespace EMMA.Native;

/// <summary>
/// Bytes the library hands to its caller: UTF-8 JSON, or binary content. The caller owns the memory and releases
/// it with <c>emma_buffer_free(data)</c>. Mirrors <c>EmmaBuffer</c> in <c>emma_native.h</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBuffer
{
    /// <summary>The first byte, or <see langword="null"/> when the buffer is empty.</summary>
    public byte* Data;

    /// <summary>The number of bytes.</summary>
    public nuint Length;

    /// <summary>Copies bytes into memory the caller can free.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The buffer; empty input gives an empty buffer.</returns>
    public static NativeBuffer Copy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return default;
        }

        byte* memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        return new NativeBuffer { Data = memory, Length = (nuint)bytes.Length };
    }
}
