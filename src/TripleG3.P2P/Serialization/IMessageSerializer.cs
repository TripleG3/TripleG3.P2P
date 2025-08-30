using TripleG3.P2P.Core;

namespace TripleG3.P2P.Serialization;

/// <summary>
/// Contract for message serializers that convert envelopes (and primitives) to / from byte sequences.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Protocol discriminator value used in the UDP header.
    /// </summary>
    SerializationProtocol Protocol { get; }

    /// <summary>
    /// Serializes a value of type <typeparamref name="T"/> into its byte representation.
    /// </summary>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes a byte span into an instance of <typeparamref name="T"/>.
    /// </summary>
    T? Deserialize<T>(ReadOnlySpan<byte> data);

    /// <summary>
    /// Deserializes a byte span into an object of the specified <paramref name="type"/>.
    /// </summary>
    object? Deserialize(Type type, ReadOnlySpan<byte> data);
}
