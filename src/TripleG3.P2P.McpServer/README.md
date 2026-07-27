# TripleG3.P2P MCP Server

A local .NET 10 stdio MCP server that gives AI models structured guidance and code-generation tools for `TripleG3.P2P`.

## Exposed capabilities

- Tools for capability discovery, configuration validation, message-contract generation, file-transfer workflow generation, and DI guidance.
- Resources for API, transport, security, and file-transfer protocol guidance.
- Prompts for topology design, peer file-transfer implementation, and connection troubleshooting.
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

## Available resources and prompts

Resources include API reference, transport comparison, security guidance, and file-transfer protocol guidance. Prompts include topology design, peer file-transfer implementation, and connection troubleshooting.

All generated configuration uses explicit endpoints and bounded limits. Network probing, listener creation, file writes, and project edits remain outside the server's default capability boundary.
