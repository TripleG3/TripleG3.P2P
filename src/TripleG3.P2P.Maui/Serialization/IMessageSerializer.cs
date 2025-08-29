using TripleG3.P2P.Maui.Core;

namespace TripleG3.P2P.Maui.Serialization;

public interface IMessageSerializer
{
    SerializationProtocol Protocol { get; }
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(ReadOnlySpan<byte> data);
    object? Deserialize(Type type, ReadOnlySpan<byte> data);
}
