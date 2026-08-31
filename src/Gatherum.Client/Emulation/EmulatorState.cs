using System.Runtime.InteropServices;

namespace Gatherum.Client.Emulation;

/// <summary>Writes a console's state into a span, or — when measuring — counts what it
/// would take without writing anything. Measuring is how a core answers
/// <see cref="IEmulatorCore.SaveStateSize"/> without keeping a second copy of the
/// arithmetic in step with the first: the size is whatever the save actually writes.
///
/// Everything is little-endian and positional. There are no names and no tags, so the
/// reader and the writer have to walk the same fields in the same order — which is what
/// the format version in the header is for.</summary>
public ref struct StateWriter
{
    private readonly Span<byte> buffer;
    private readonly bool measuring;
    private int position;

    public StateWriter(Span<byte> buffer)
    {
        this.buffer = buffer;
        measuring = false;
    }

    private StateWriter(bool measuring)
    {
        buffer = Span<byte>.Empty;
        this.measuring = measuring;
    }

    /// <summary>A writer that counts instead of writing.</summary>
    public static StateWriter Measure() => new(measuring: true);

    /// <summary>How many bytes have been written, or would have been.</summary>
    public readonly int Length => position;

    /// <summary>Set when the buffer ran out. A save that overflowed is not a save.</summary>
    public bool Failed { get; private set; }

    private Span<byte> Take(int count)
    {
        var at = position;
        position += count;
        if (measuring)
            return Span<byte>.Empty;
        if (at + count > buffer.Length)
        {
            Failed = true;
            return Span<byte>.Empty;
        }
        return buffer.Slice(at, count);
    }

    public void Write(bool value) => Write((byte)(value ? 1 : 0));

    public void Write(byte value)
    {
        var span = Take(1);
        if (span.Length == 1)
            span[0] = value;
    }

    public void Write(ushort value)
    {
        var span = Take(2);
        if (span.Length == 2)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span, value);
    }

    public void Write(int value)
    {
        var span = Take(4);
        if (span.Length == 4)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
    }

    public void Write(long value)
    {
        var span = Take(8);
        if (span.Length == 8)
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span, value);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        var span = Take(bytes.Length);
        if (span.Length == bytes.Length)
            bytes.CopyTo(span);
    }

    public void Write(ReadOnlySpan<int> values) =>
        Write(MemoryMarshal.AsBytes(values));
}

/// <summary>Reads back what <see cref="StateWriter"/> wrote, in the same order. A short
/// or malformed state sets <see cref="Failed"/> and yields zeroes rather than throwing:
/// a state that will not load is a message to the player, not a crash.</summary>
public ref struct StateReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> buffer = buffer;
    private int position;

    public bool Failed { get; private set; }

    private ReadOnlySpan<byte> Take(int count)
    {
        var at = position;
        position += count;
        if (at + count > buffer.Length)
        {
            Failed = true;
            return ReadOnlySpan<byte>.Empty;
        }
        return buffer.Slice(at, count);
    }

    /// <summary>Steps past bytes whose value has already been checked — the format tag
    /// at the head of a state, which the caller compares before it starts reading.</summary>
    public void Skip(int count) => Take(count);

    public bool ReadBool() => ReadByte() != 0;

    public byte ReadByte()
    {
        var span = Take(1);
        return span.Length == 1 ? span[0] : (byte)0;
    }

    public ushort ReadUInt16()
    {
        var span = Take(2);
        return span.Length == 2
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span)
            : (ushort)0;
    }

    public int ReadInt32()
    {
        var span = Take(4);
        return span.Length == 4
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span)
            : 0;
    }

    public long ReadInt64()
    {
        var span = Take(8);
        return span.Length == 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span)
            : 0;
    }

    public void Read(Span<byte> destination)
    {
        var span = Take(destination.Length);
        if (span.Length == destination.Length)
            span.CopyTo(destination);
        else
            destination.Clear();
    }

    public void Read(Span<int> destination) => Read(MemoryMarshal.AsBytes(destination));
}
