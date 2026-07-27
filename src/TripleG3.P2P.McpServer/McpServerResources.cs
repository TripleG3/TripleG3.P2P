using ModelContextProtocol.Server;
using System.ComponentModel;

namespace TripleG3.P2P.McpServer;

[McpServerResourceType]
public static class McpServerResources
{
    [McpServerResource(UriTemplate = "p2p://api-reference", Name = "P2P API reference"), Description("Core TripleG3.P2P API guidance.")]
    public static string ApiReference() => "Use SerialBusFactory.CreateUdp or CreateTcp for live messaging. Use PeerFileTransferClient for direct peer-to-peer file transfer. Use LengthPrefixed for new contracts.";

    [McpServerResource(UriTemplate = "p2p://transport-comparison", Name = "Transport comparison"), Description("Transport selection guidance.")]
    public static string TransportComparison() => "UDP is low latency and best effort. TCP is reliable and ordered per connection. File transfer is a separate direct TCP streaming protocol with receiver consent.";

    [McpServerResource(UriTemplate = "p2p://security-guidance", Name = "Security guidance"), Description("Security guidance for generated P2P code.")]
    public static string SecurityGuidance() => "Use explicit peer allowlists, LengthPrefixed serialization, payload/file limits, cancellation, SHA-256 verification, and sandboxed destination paths. Do not expose listeners publicly without authentication and authorization.";

    [McpServerResource(UriTemplate = "p2p://file-transfer-protocol", Name = "File transfer protocol"), Description("Direct peer-to-peer file transfer behavior.")]
    public static string FileTransferProtocol() => "The sender offers a file over a dedicated TCP connection. The receiver's TransferRequested event must explicitly accept with a destination path or reject. Transfers stream through a temporary .part file and verify SHA-256 before completion.";
}
