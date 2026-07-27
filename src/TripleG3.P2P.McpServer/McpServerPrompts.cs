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
