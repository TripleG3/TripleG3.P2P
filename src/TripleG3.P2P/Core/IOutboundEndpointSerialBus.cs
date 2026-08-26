using System.Net;

namespace TripleG3.P2P.Core;

/// <summary>
/// Optional serial-bus capability for changing outbound peer endpoints while the bus is listening.
/// </summary>
public interface IOutboundEndpointSerialBus : ISerialBus
{
    /// <summary>
    /// Gets a snapshot of the endpoints that will receive future outbound messages.
    /// </summary>
    IReadOnlyCollection<IPEndPoint> OutboundEndPoints { get; }

    /// <summary>
    /// Adds an endpoint to receive future outbound messages.
    /// </summary>
    /// <returns><see langword="true"/> when the endpoint was added; otherwise <see langword="false"/> when it was already present.</returns>
    bool AddOutboundEndPoint(IPEndPoint endpoint);

    /// <summary>
    /// Removes an endpoint from future outbound messages.
    /// </summary>
    /// <returns><see langword="true"/> when the endpoint was removed; otherwise <see langword="false"/> when it was not present.</returns>
    bool RemoveOutboundEndPoint(IPEndPoint endpoint);
}