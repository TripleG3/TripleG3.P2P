# TripleG3.P2P MCP server guidance

The MCP server under `src/TripleG3.P2P.McpServer` uses the official C# SDK:

- https://github.com/modelcontextprotocol/csharp-sdk
- https://csharp.sdk.modelcontextprotocol.io/

Use stdio transport for local VS Code integration. Keep tools read-only or code-generating by default; do not open network listeners, transfer files, modify project files, or execute generated code without an explicit future capability and confirmation design.
