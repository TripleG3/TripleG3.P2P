using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace TripleG3.P2P.McpServer;

[McpServerToolType]
public static class McpServerTools
{
    [McpServerTool(Name = "p2p_list_capabilities"), Description("Lists the transports, serializers, video status, and peer-to-peer file-transfer capabilities provided by TripleG3.P2P.")]
    public static object ListCapabilities() => new
    {
        transports = new[] { "UDP", "TCP" },
        serializers = new[] { "None", "JsonRaw", "LengthPrefixed" },
        fileTransfer = new[] { "direct TCP peer-to-peer", "receiver consent", "SHA-256", "cancellation", "progress" },
        video = "experimental",
        ftp = "not supported"
    };

    [McpServerTool(Name = "p2p_validate_protocol_configuration"), Description("Validates a P2P protocol configuration without opening a socket.")]
    public static object ValidateProtocolConfiguration(
        [Description("Transport name: udp or tcp.")] string transport,
        [Description("Local address, for example 127.0.0.1.")] string localAddress,
        [Description("Local TCP or UDP port.")] int localPort,
        [Description("Remote peer address.")] string remoteAddress,
        [Description("Remote peer port.")] int remotePort,
        [Description("Serializer: None, JsonRaw, or LengthPrefixed.")] string serializationProtocol = "LengthPrefixed")
    {
        var errors = new List<string>();
        if (!transport.Equals("udp", StringComparison.OrdinalIgnoreCase) && !transport.Equals("tcp", StringComparison.OrdinalIgnoreCase)) errors.Add("Transport must be udp or tcp.");
        if (!IPAddress.TryParse(localAddress, out _)) errors.Add("Local address is not a valid IP address.");
        if (!IPAddress.TryParse(remoteAddress, out _)) errors.Add("Remote address is not a valid IP address.");
        if (localPort is < 1 or > 65535) errors.Add("Local port must be between 1 and 65535.");
        if (remotePort is < 1 or > 65535) errors.Add("Remote port must be between 1 and 65535.");
        if (!Enum.TryParse<TripleG3.P2P.Core.SerializationProtocol>(serializationProtocol, true, out _)) errors.Add("Unknown serialization protocol.");
        return new { valid = errors.Count == 0, errors };
    }

    [McpServerTool(Name = "p2p_generate_message_contract"), Description("Generates a C# record with TripleG3.P2P message contract attributes.")]
    public static string GenerateMessageContract(
        [Description("Record type name.")] string typeName,
        [Description("Comma-separated fields such as 'string Name, int Age'.")] string fields,
        [Description("Protocol message name.")] string protocolName)
    {
        var parsed = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parameters = parsed.Select((field, index) =>
        {
            var parts = field.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new ArgumentException($"Invalid field '{field}'. Use 'type name'.");
            return $"[property: Udp({index + 1})] {parts[0]} {parts[1]}";
        });
        return $"[P2PMessage(\"{Escape(protocolName)}\")]\npublic sealed record {typeName}({string.Join(", ", parameters)});";
    }

    [McpServerTool(Name = "p2p_generate_file_transfer_workflow"), Description("Generates receiver-consent and cancellable peer-to-peer file transfer code.")]
    public static string GenerateFileTransferWorkflow(
        [Description("Receiver listen port.")] int receiverPort,
        [Description("Sender listen port.")] int senderPort,
        [Description("Destination directory expression.")] string destinationDirectory = "receivedDirectory")
    {
        if (receiverPort is < 1 or > 65535 || senderPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(receiverPort));
        return $$"""
        var receiver = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, {{receiverPort}})
        });
        receiver.TransferRequested += (request, cancellationToken) =>
            new ValueTask<FileTransferDecision>(FileTransferDecision.Accept(
                Path.Combine({{destinationDirectory}}, request.FileName)));
        await receiver.StartAsync(cancellationToken);

        var sender = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, {{senderPort}})
        });
        await sender.StartAsync(cancellationToken);
        var results = await sender.SendAsync(
            sourcePath,
            [new IPEndPoint(IPAddress.Loopback, {{receiverPort}})],
            cancellationToken: cancellationToken);
        """;
    }

    [McpServerTool(Name = "p2p_generate_di_registration"), Description("Generates dependency-injection registration guidance for TripleG3.P2P.")]
    public static string GenerateDiRegistration() => """
    services.AddP2PUdp();
    services.AddSingleton<IFileTransferClient>(_ => new PeerFileTransferClient(new FileTransferOptions
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 9100)
    }));
    """;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
