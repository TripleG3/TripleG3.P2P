using System.Buffers.Binary;

namespace TripleG3.P2P.Core;

/// <summary>
/// Fixed-size (8 bytes) UDP header containing payload length, message type and serialization protocol identifier.
/// </summary>
internal readonly record struct UdpHeader(int Length, short MessageType, SerializationProtocol SerializationProtocol)
{
    internal static UdpHeader Empty { get; } = new(0, 0, SerializationProtocol.None);

    /// <summary>
    /// Size of the header in bytes.
    /// </summary>
    internal const int Size = 8; // bytes 0-7

    /// <summary>
    /// Writes the header fields into the destination span (little endian encoding).
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Destination too small for header");
        BinaryPrimitives.WriteInt32LittleEndian(destination, Length);
        BinaryPrimitives.WriteInt16LittleEndian(destination[4..], (short)MessageType);
        BinaryPrimitives.WriteInt16LittleEndian(destination[6..], (short)SerializationProtocol);
    }

    /// <summary>
    /// Reads a header from the provided source span; returns <see cref="Empty"/> if insufficient bytes.
    /// </summary>
    public static UdpHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) return Empty;
        var len = BinaryPrimitives.ReadInt32LittleEndian(source);
        var msgType = BinaryPrimitives.ReadInt16LittleEndian(source[4..]);
        var proto = (SerializationProtocol)BinaryPrimitives.ReadInt16LittleEndian(source[6..]);
        return new(len, msgType, proto);
    }
}
