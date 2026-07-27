# TripleG3.P2P MCP Server

A local stdio MCP server that gives AI models structured guidance and code-generation tools for `TripleG3.P2P`.

## Exposed capabilities

- Tools for capability discovery, configuration validation, message-contract generation, file-transfer workflow generation, and DI guidance.
- Resources for API, transport, security, and file-transfer protocol guidance.
- Prompts for topology design, peer file-transfer implementation, and connection troubleshooting.

The server does not open network listeners, transfer files, modify project files, or execute generated code. Generated code is returned as text for review.

## Run

From the repository root:

```text
dotnet run --project src/TripleG3.P2P.McpServer/TripleG3.P2P.McpServer.csproj --no-launch-profile
```

VS Code configuration is in `.vscode/mcp.json`. The MCP C# SDK is documented at https://csharp.sdk.modelcontextprotocol.io/.
