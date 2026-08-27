# TripleG3.P2P MCP Server

A local .NET 10 stdio MCP server that gives AI models structured guidance and code-generation tools for `TripleG3.P2P`.

## Exposed capabilities

- Tools for complete capability discovery, protocol and connected-device option validation, message-contract generation, connected-device and live-session workflows, file transfer, reusable peer-transfer sessions, and DI guidance.
- Resources for API, transport, security, connected-device membership, live-session control, and transfer protocols.
- Prompts for topology design, connected-device hubs, live sessions, peer file transfer, and connection troubleshooting.
- The server is built with the official `ModelContextProtocol` C# SDK.

The server does not open network listeners, transfer files, modify project files, or execute generated code. Generated code is returned as text for review.

It is not part of the `TripleG3.P2P` NuGet package. Consumers who only need UDP, TCP, serialization, video, or peer-to-peer file transfer should install the core package instead.

## Run

From the repository root:

```text
dotnet run --project src/TripleG3.P2P.McpServer/TripleG3.P2P.McpServer.csproj --no-launch-profile
```

VS Code configuration is in `.vscode/mcp.json`. The MCP C# SDK is documented in the [official SDK documentation](https://csharp.sdk.modelcontextprotocol.io/).

## Available tools

- `p2p_list_capabilities`
- `p2p_validate_protocol_configuration`
- `p2p_generate_message_contract`
- `p2p_generate_file_transfer_workflow`
- `p2p_generate_di_registration`
- `p2p_validate_connected_device_hub_options`
- `p2p_generate_connected_device_hub_workflow`
- `p2p_generate_live_session_workflow`
- `p2p_generate_peer_transfer_session_workflow`

## Available resources and prompts

Resources include API reference, transport comparison, security guidance, connected-device hub boundaries, live-session control, and file/peer-transfer protocols. Prompts include topology design, connected-device integration, live-session design, peer file-transfer implementation, and connection troubleshooting.

All generated configuration uses explicit endpoints and bounded limits. Network probing, listener creation, file writes, and project edits remain outside the server's default capability boundary.
