using TripleG3.P2P.Serialization;
using TripleG3.P2P.Udp;
using TripleG3.P2P.Tcp;

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

    /// <summary>
    /// Creates a TCP serial bus (reliable stream) using the same serializers.
    /// </summary>
    public static ISerialBus CreateTcp()
        => new TcpSerialBus(new IMessageSerializer[]
        {
            new NoneMessageSerializer(),
            new JsonRawMessageSerializer()
        });

    /// <summary>
    /// Placeholder for future FTP-based transport (likely will be a control channel + file payload hybrid).
    /// Currently not implemented.
    /// </summary>
    public static ISerialBus CreateFtp() => throw new NotImplementedException("FTP transport not yet implemented.");
}
