using TripleG3.P2P.Serialization;
using TripleG3.P2P.Udp;

namespace TripleG3.P2P.Core;

/// <summary>
/// Factory helpers for constructing <see cref="ISerialBus"/> implementations.
/// </summary>
public static class SerialBusFactory
{
    /// <summary>
    /// Creates a UDP serial bus configured with the built-in serializers (None / JsonRaw).
    /// </summary>
    public static ISerialBus CreateUdp()
        => new UdpSerialBus(new IMessageSerializer[]
        {
            new NoneMessageSerializer(),
            new JsonRawMessageSerializer()
        });
}
