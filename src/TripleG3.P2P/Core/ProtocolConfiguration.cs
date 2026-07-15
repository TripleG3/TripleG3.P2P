using System.Net;

namespace TripleG3.P2P.Core;

/// <summary>
/// Transport + serialization configuration applied when starting a serial bus.
/// Immutable (init-only) to encourage stable configuration for the lifetime of a bus instance.
/// </summary>
public sealed class ProtocolConfiguration
{
    /// <summary>
    /// Local address used for the listening socket.
    /// </summary>
    public IPAddress LocalAddress { get; init; } = IPAddress.Any;

    /// <summary>
    /// Remote peer endpoint to which outbound messages are sent.
    /// </summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// Optional additional endpoints that will also receive every outbound message (broadcast).
    /// The <see cref="RemoteEndPoint"/> is always included implicitly; duplicates are de-duplicated
    /// by endpoint string representation (address:port).
    /// </summary>
    public IReadOnlyCollection<IPEndPoint> BroadcastEndPoints { get; init; } = [];

    /// <summary>
    /// Local UDP port to bind for inbound messages.
    /// </summary>
    public required int LocalPort { get; init; }

    /// <summary>
    /// Maximum accepted serialized payload size. Frames above this limit are rejected before allocation.
    /// </summary>
    public int MaxPayloadBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum simultaneous accepted TCP sessions. This setting is ignored by UDP.
    /// </summary>
    public int MaxInboundConnections { get; init; } = 64;

    /// <summary>
    /// Serialization protocol used for every message on this bus.
    /// </summary>
    public SerializationProtocol SerializationProtocol { get; init; } = SerializationProtocol.None;
}
