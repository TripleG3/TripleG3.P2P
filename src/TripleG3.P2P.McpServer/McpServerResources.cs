using ModelContextProtocol.Server;
using System.ComponentModel;

namespace TripleG3.P2P.McpServer;

[McpServerResourceType]
public static class McpServerResources
{
    [McpServerResource(UriTemplate = "p2p://api-reference", Name = "P2P API reference"), Description("Core TripleG3.P2P API guidance.")]
    public static string ApiReference() => "Use SerialBusFactory.CreateUdp or CreateTcp for typed messaging. Use ConnectedDeviceHub<TDeviceDescriptor,TConnectionRoute,TStreamDescriptor> for in-memory membership and dispatch planning. Use PeerFileTransferClient for direct file transfer or IPeerTransferSession for reusable multiplexed transfers. Use LengthPrefixed for new contracts.";

    [McpServerResource(UriTemplate = "p2p://transport-comparison", Name = "Transport comparison"), Description("Transport selection guidance.")]
    public static string TransportComparison() => "UDP is low latency and best effort. TCP is reliable and ordered per connection. File transfer is a separate direct TCP streaming protocol with receiver consent.";

    [McpServerResource(UriTemplate = "p2p://security-guidance", Name = "Security guidance"), Description("Security guidance for generated P2P code.")]
    public static string SecurityGuidance() => "Use explicit peer allowlists, LengthPrefixed serialization, payload/file limits, cancellation, SHA-256 verification, and sandboxed destination paths. Do not expose listeners publicly without authentication and authorization.";

    [McpServerResource(UriTemplate = "p2p://file-transfer-protocol", Name = "File transfer protocol"), Description("Direct peer-to-peer file transfer behavior.")]
    public static string FileTransferProtocol() => "The sender offers a file over a dedicated TCP connection. The receiver's TransferRequested event must explicitly accept with a destination path or reject. Transfers stream through a temporary .part file and verify SHA-256 before completion.";

    [McpServerResource(UriTemplate = "p2p://connected-device-hub", Name = "Connected device hub"), Description("Boundary and usage guidance for generic connected-device membership and routing.")]
    public static string ConnectedDeviceHub() => "ConnectedDeviceHub is synchronous and in-memory. Hosts add already-trusted DeviceConnection values, query immutable snapshots, and publish returned ConnectedDeviceDispatch plans through a selected transport. Stable DeviceId and replaceable ConnectionId values prevent stale disconnects and routes from affecting a reconnect. The hub does not authenticate, persist, execute payloads, or open network connections.";

    [McpServerResource(UriTemplate = "p2p://live-session-control", Name = "Live session control"), Description("Transport-neutral live-session state and data-plane ownership guidance.")]
    public static string LiveSessionControl() => "ConnectedDeviceHub coordinates offer, answer, start, active, stop, stopped, and failure states. Stream kind and descriptor values are host-defined and opaque. Hosts publish control dispatches and own file, audio, video, screen-capture, rendering, and remote-input data planes. Disconnecting or replacing a participating connection fails the session and revokes stale routes.";

    [McpServerResource(UriTemplate = "p2p://peer-transfer-session", Name = "Peer transfer session"), Description("Reusable multiplexed peer-transfer session guidance.")]
    public static string PeerTransferSession() => "IPeerTransferSession carries multiple independent raw-text or file transfers over one reusable TCP session. It supports open acceptance, bounded queues, progress snapshots, status queries, cancellation acknowledgement, and heartbeats. The host owns authorization, payload interpretation, and secure session-grant provisioning.";
}
