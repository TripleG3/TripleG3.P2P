using System.Net;
using TripleG3.P2P.Maui.Core;

namespace TripleG3.P2P.Maui.Core;

/// <summary>
/// Transport + serialization configuration applied when starting a serial bus.
/// Immutable (init-only) to encourage stable configuration for the lifetime of a bus instance.
/// </summary>
public sealed class ProtocolConfiguration
{
    /// <summary>
    /// Remote peer endpoint to which outbound messages are sent.
    /// </summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// Local UDP port to bind for inbound messages.
    /// </summary>
    public required int LocalPort { get; init; }

    /// <summary>
    /// Serialization protocol used for every message on this bus.
    /// </summary>
    public SerializationProtocol SerializationProtocol { get; init; } = SerializationProtocol.None;
}
