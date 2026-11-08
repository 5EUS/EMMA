using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace EMMA.PluginHost.Signing;

/// <summary>
/// Builds the bytes that get hashed or signed. Every value is written with a one-byte type tag
/// and, for variable-size values, a four-byte length, so one field can never be read as part of
/// another (a value containing a newline or a delimiter cannot shift fields the way a joined text
/// payload can). Builders and verifiers share this class, so they cannot drift apart.
/// </summary>
internal sealed class FrameWriter
{
    private readonly ArrayBufferWriter<byte> buffer = new();

    /// <summary>Writes a text value.</summary>
    /// <param name="value">The text.</param>
    /// <returns>This writer.</returns>
    public FrameWriter Text(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        this.Tag((byte)'S');
        this.Length(bytes.Length);
        this.buffer.Write(bytes);
        return this;
    }

    /// <summary>Writes an optional text value; absence is distinct from an empty string.</summary>
    /// <param name="value">The text, or <see langword="null"/>.</param>
    /// <returns>This writer.</returns>
    public FrameWriter OptionalText(string? value)
    {
        if (value is null)
        {
            this.Tag((byte)'N');
            return this;
        }

        return this.Text(value);
    }

    /// <summary>Writes raw bytes.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>This writer.</returns>
    public FrameWriter Bytes(ReadOnlySpan<byte> value)
    {
        this.Tag((byte)'X');
        this.Length(value.Length);
        this.buffer.Write(value);
        return this;
    }

    /// <summary>Writes a whole number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>This writer.</returns>
    public FrameWriter Integer(long value)
    {
        this.Tag((byte)'I');
        BinaryPrimitives.WriteInt64BigEndian(this.buffer.GetSpan(8), value);
        this.buffer.Advance(8);
        return this;
    }

    /// <summary>Writes an optional whole number; absence is distinct from zero.</summary>
    /// <param name="value">The number, or <see langword="null"/>.</param>
    /// <returns>This writer.</returns>
    public FrameWriter OptionalInteger(long? value)
    {
        if (value is null)
        {
            this.Tag((byte)'N');
            return this;
        }

        return this.Integer(value.Value);
    }

    /// <summary>Writes a boolean.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>This writer.</returns>
    public FrameWriter Flag(bool value)
    {
        this.Tag((byte)'B');
        this.Tag(value ? (byte)1 : (byte)0);
        return this;
    }

    /// <summary>Writes a list of text values, with its count.</summary>
    /// <param name="values">The values, in order.</param>
    /// <returns>This writer.</returns>
    public FrameWriter TextList(IReadOnlyList<string> values)
    {
        this.Tag((byte)'L');
        this.Length(values.Count);
        foreach (string value in values)
        {
            this.Text(value);
        }

        return this;
    }

    /// <summary>Gets the bytes written so far.</summary>
    /// <returns>A copy of the framed bytes.</returns>
    public byte[] ToArray() => this.buffer.WrittenSpan.ToArray();

    private void Tag(byte tag)
    {
        this.buffer.GetSpan(1)[0] = tag;
        this.buffer.Advance(1);
    }

    private void Length(int length)
    {
        BinaryPrimitives.WriteInt32BigEndian(this.buffer.GetSpan(4), length);
        this.buffer.Advance(4);
    }
}
