using TripleG3.P2P.Maui.Serialization;
using TripleG3.P2P.Maui.Udp;

namespace TripleG3.P2P.Maui.Core;

public static class SerialBusFactory
{
    public static ISerialBus CreateUdp()
        => new UdpSerialBus(new IMessageSerializer[]
        {
            new NoneMessageSerializer(),
            new JsonRawMessageSerializer()
        });
}
