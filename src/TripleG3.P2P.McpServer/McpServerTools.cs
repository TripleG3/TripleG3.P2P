using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace TripleG3.P2P.McpServer;

[McpServerToolType]
public static class McpServerTools
{
    [McpServerTool(Name = "p2p_list_capabilities"), Description("Lists the transports, serializers, hubs, media, and transfer capabilities provided by TripleG3.P2P.")]
    public static object ListCapabilities() => new
    {
        transports = new[] { "UDP", "TCP" },
        serializers = new[] { "None", "JsonRaw", "LengthPrefixed" },
        hubs = new[] { "ConnectedDevice", "Chat", "HostedChat", "GamingLobby", "Notifications", "VideoChat" },
        connectedDeviceHub = new[] { "in-memory membership", "direct routing", "fan-out", "route revocation", "live-session control" },
        fileTransfer = new[] { "direct TCP peer-to-peer", "receiver consent", "SHA-256", "cancellation", "progress", "bounded streaming" },
        peerTransfer = new[] { "reusable session", "multiplexed transfers", "raw text", "file", "query", "cancellation acknowledgement", "heartbeat" },
        audio = "Opus frames over RTP",
        video = "experimental H.264 over RTP",
        notifications = "in-process device routing and platform projection",
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
            return $"[property: P2PProperty({index + 1})] {parts[0]} {parts[1]}";
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
    services.AddP2PHubs();
    services.AddSingleton<IFileTransferClient>(_ => new PeerFileTransferClient(new FileTransferOptions
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 9100)
    }));
    """;

    [McpServerTool(Name = "p2p_validate_connected_device_hub_options"), Description("Validates bounds for an in-memory ConnectedDeviceHub configuration.")]
    public static object ValidateConnectedDeviceHubOptions(
        [Description("Maximum simultaneously connected devices.")] int maximumConnectedDevices = 256,
        [Description("Maximum active live sessions.")] int maximumSessions = 256,
        [Description("Maximum retained terminal live sessions.")] int maximumRetainedTerminalSessions = 256,
        [Description("Maximum retained membership changes.")] int membershipHistoryCapacity = 200,
        [Description("Maximum retained retired session identifiers.")] int maximumRetiredSessionIds = 4096)
    {
        var errors = new List<string>();
        if (maximumConnectedDevices <= 0) errors.Add("MaximumConnectedDevices must be greater than zero.");
        if (maximumSessions <= 0) errors.Add("MaximumSessions must be greater than zero.");
        if (maximumRetainedTerminalSessions < 0) errors.Add("MaximumRetainedTerminalSessions cannot be negative.");
        if (membershipHistoryCapacity < 0) errors.Add("MembershipHistoryCapacity cannot be negative.");
        if (maximumRetiredSessionIds <= 0) errors.Add("MaximumRetiredSessionIds must be greater than zero.");
        return new
        {
            valid = errors.Count == 0,
            errors,
            boundary = "The hub is in-memory and produces dispatch plans. The host owns trust, persistence, network publication, and data-plane execution."
        };
    }

    [McpServerTool(Name = "p2p_generate_connected_device_hub_workflow"), Description("Generates a generic ConnectedDeviceHub membership, routing, and stale-route workflow.")]
    public static string GenerateConnectedDeviceHubWorkflow(
        [Description("C# type used as the application-owned device descriptor.")] string descriptorType = "DeviceDescriptor",
        [Description("C# type used as the host-owned connection route.")] string routeType = "IPEndPoint",
        [Description("C# type used as the host-owned live stream descriptor.")] string streamDescriptorType = "StreamDescriptor")
    {
        ValidateTypeName(descriptorType, nameof(descriptorType));
        ValidateTypeName(routeType, nameof(routeType));
        ValidateTypeName(streamDescriptorType, nameof(streamDescriptorType));
        return $$"""
        using var hub = new ConnectedDeviceHub<{{descriptorType}}, {{routeType}}, {{streamDescriptorType}}>(Guid.NewGuid());

        var localConnection = new DeviceConnection(localDeviceId, localConnectionId);
        var remoteConnection = new DeviceConnection(remoteDeviceId, remoteConnectionId);
        hub.Connect(localConnection, localDescriptor, localRoute);
        hub.Connect(remoteConnection, remoteDescriptor, remoteRoute);

        ConnectedDeviceDispatch<RemoteRequest, {{routeType}}> dispatch =
            hub.RouteTo(localConnection, remoteDeviceId, request);

        foreach (ConnectedDeviceRoute<{{routeType}}> recipient in dispatch.Recipients)
        {
            if (!hub.IsRouteCurrent(recipient)) continue;
            // The host publishes dispatch.Message using recipient.Route.
        }
        """;
    }

    [McpServerTool(Name = "p2p_generate_live_session_workflow"), Description("Generates transport-neutral ConnectedDeviceHub live-session control code.")]
    public static string GenerateLiveSessionWorkflow(
        [Description("Stream kind such as audio, video, screen, file, or input.")] string streamKind,
        [Description("Direction: Send, Receive, or Bidirectional.")] string direction = "Bidirectional")
    {
        if (string.IsNullOrWhiteSpace(streamKind)) throw new ArgumentException("A stream kind is required.", nameof(streamKind));
        if (!Enum.TryParse<TripleG3.P2P.Hubs.LiveStreamDirection>(direction, true, out var parsedDirection))
        {
            throw new ArgumentException("Direction must be Send, Receive, or Bidirectional.", nameof(direction));
        }
        return $$"""
        var sessionId = Guid.NewGuid();
        var stream = new LiveStreamDescriptor<StreamDescriptor>(
            Guid.NewGuid(),
            "{{Escape(streamKind.Trim())}}",
            LiveStreamDirection.{{parsedDirection}},
            streamDescriptor);

        var offer = hub.OfferSession(originConnection, remoteDeviceId, sessionId, [stream]);
        // Publish offer.Message to offer.Recipients using the host-selected transport.

        var answer = hub.AnswerSession(remoteConnection, sessionId, LiveSessionAnswer.Accept, [stream]);
        var start = hub.StartSession(originConnection, sessionId);
        var active = hub.ActivateSession(remoteConnection, sessionId);

        // The host now starts the selected file, audio, video, screen, or input data plane.

        var stopping = hub.StopSession(originConnection, sessionId, "Stopping");
        var stopped = hub.CompleteStopSession(remoteConnection, sessionId, "Stopped");
        """;
    }

    [McpServerTool(Name = "p2p_generate_peer_transfer_session_workflow"), Description("Generates a reusable multiplexed peer-transfer session workflow for host-owned payloads.")]
    public static string GeneratePeerTransferSessionWorkflow() => """
    IPeerTransferSession session = await sessionFactory.ConnectAsync(
        remoteEndPoint,
        new PeerTransferSessionOptions(sessionId, localDeviceId, remoteDeviceId, sessionGrant)
        {
            Authorizer = authorizer
        },
        cancellationToken);

    var descriptor = new PeerTransferDescriptor(
        Guid.NewGuid(),
        PeerTransferKind.RawText,
        "payload",
        "Application-owned payload",
        payload.Length,
        integrityHash);

    IPeerTransfer transfer = await session.OpenTransferAsync(descriptor, cancellationToken);
    await transfer.SendAsync(payload, cancellationToken);
    await transfer.CompleteAsync(cancellationToken);
    """;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ValidateTypeName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A type name is required.", parameterName);
        if (value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.' or '<' or '>' or ',' or ' ' or '[' or ']')))
        {
            throw new ArgumentException("The type name contains unsupported characters.", parameterName);
        }
    }
}
