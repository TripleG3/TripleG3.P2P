using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace TripleG3.P2P.McpServer;

[McpServerPromptType]
public static class McpServerPrompts
{
    [McpServerPrompt(Name = "design_p2p_topology"), Description("Guides an AI model through selecting UDP, TCP, and direct file-transfer peers.")]
    public static GetPromptResult DesignTopology([Description("Describe peers, latency, reliability, and file-transfer needs.")] string requirements) => Prompt("Design a TripleG3.P2P topology", $"Design a secure topology for these requirements:\n{requirements}\nPrefer explicit endpoints, LengthPrefixed serialization, and separate file transfer for large payloads.");

    [McpServerPrompt(Name = "implement_peer_file_transfer"), Description("Guides implementation of receiver-consent and cancellable peer file transfer.")]
    public static GetPromptResult ImplementFileTransfer([Description("Describe the sender, receiver, paths, limits, and cancellation requirements.")] string requirements) => Prompt("Implement peer file transfer", $"Implement direct PeerFileTransferClient workflows for:\n{requirements}\nThe receiver must explicitly accept or reject, and both sides must support cancellation and integrity verification.");

    [McpServerPrompt(Name = "debug_p2p_connection"), Description("Guides diagnosis of a TripleG3.P2P connection problem.")]
    public static GetPromptResult DebugConnection([Description("Describe the error and endpoint configuration.")] string symptoms) => Prompt("Debug P2P connection", $"Diagnose this TripleG3.P2P issue:\n{symptoms}\nCheck endpoint validity, listener lifecycle, serializer agreement, payload limits, cancellation, and peer authorization.");

    [McpServerPrompt(Name = "design_connected_device_hub"), Description("Guides design of generic in-memory device membership, dispatch routing, and reconnect handling.")]
    public static GetPromptResult DesignConnectedDeviceHub([Description("Describe device descriptors, routes, membership limits, and message flows.")] string requirements) => Prompt("Design a connected-device hub", $"Design a TripleG3.P2P ConnectedDeviceHub integration for:\n{requirements}\nKeep authentication, approval, persistence, queues, tools, network publication, and payload execution in the host. Use stable device IDs, replaceable connection IDs, revisioned snapshots, and stale-route checks.");

    [McpServerPrompt(Name = "design_live_session"), Description("Guides transport-neutral live-session control and data-plane selection.")]
    public static GetPromptResult DesignLiveSession([Description("Describe participants, stream kinds, directions, and stop/failure behavior.")] string requirements) => Prompt("Design a P2P live session", $"Design live-session control for:\n{requirements}\nUse ConnectedDeviceHub only for offer/answer/start/stop/failure dispatch planning. Select file transfer, peer transfer, RTP audio, or RTP video for the host-owned data plane. Keep authentication, consent, capture, rendering, and remote-input execution outside the package.");

    private static GetPromptResult Prompt(string description, string text) => new()
    {
        Description = description,
        Messages = [new()
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = text }
        }]
    };
}
