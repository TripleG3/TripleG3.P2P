using System.Buffers.Binary;
using TripleG3.P2P.Maui.Attributes;

namespace TripleG3.P2P.Maui.Core;

internal readonly record struct UdpHeader(int Length, short MessageType, SerializationProtocol SerializationProtocol)
{
    internal static UdpHeader Empty { get; } = new(0, 0, SerializationProtocol.None);

    internal const int Size = 8; // bytes 0-7

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Destination too small for header");
        // Length (int32)
        BinaryPrimitives.WriteInt32LittleEndian(destination, Length);
        // MessageType (int16)
        BinaryPrimitives.WriteInt16LittleEndian(destination[4..], (short)MessageType);
        // SerializationProtocol (int16)
        BinaryPrimitives.WriteInt16LittleEndian(destination[6..], (short)SerializationProtocol);
    }

    public static UdpHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) return Empty;
        var len = BinaryPrimitives.ReadInt32LittleEndian(source);
        var msgType = BinaryPrimitives.ReadInt16LittleEndian(source[4..]);
        var proto = (SerializationProtocol)BinaryPrimitives.ReadInt16LittleEndian(source[6..]);
        return new(len, msgType, proto);
    }
}
