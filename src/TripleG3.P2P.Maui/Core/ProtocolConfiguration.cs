using System.Net;
using TripleG3.P2P.Maui.Core;

namespace TripleG3.P2P.Maui.Core;

public sealed class ProtocolConfiguration
{
    public required IPEndPoint RemoteEndPoint { get; init; }
    public required int LocalPort { get; init; }
    public SerializationProtocol SerializationProtocol { get; init; } = SerializationProtocol.None;
}
