using System.Reflection;
using System.Text.Json;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class McpServerToolsTests
{
    private static readonly Type ToolsType = Type.GetType(
        "TripleG3.P2P.McpServer.McpServerTools, TripleG3.P2P.McpServer",
        throwOnError: true)!;

    [Fact]
    public void Capabilities_Include_Connected_Devices_Media_And_Peer_Transfers()
    {
        object capabilities = Invoke("ListCapabilities")!;
        string json = JsonSerializer.Serialize(capabilities);

        Assert.Contains("ConnectedDevice", json, StringComparison.Ordinal);
        Assert.Contains("Opus", json, StringComparison.Ordinal);
        Assert.Contains("H.264", json, StringComparison.Ordinal);
        Assert.Contains("multiplexed transfers", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_Device_Options_Report_Invalid_Bounds()
    {
        object result = Invoke("ValidateConnectedDeviceHubOptions", 0, 0, -1, -1, 0)!;
        string json = JsonSerializer.Serialize(result);

        Assert.Contains("\"valid\":false", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaximumConnectedDevices", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_Device_Workflow_Preserves_Generic_Host_Boundary()
    {
        string generated = Assert.IsType<string>(Invoke(
            "GenerateConnectedDeviceHubWorkflow",
            "DeviceDescriptor",
            "IPEndPoint",
            "StreamDescriptor"));

        Assert.Contains("ConnectedDeviceHub<DeviceDescriptor, IPEndPoint, StreamDescriptor>", generated, StringComparison.Ordinal);
        Assert.Contains("IsRouteCurrent", generated, StringComparison.Ordinal);
        Assert.Contains("host publishes", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Live_Session_Workflow_Uses_Explicit_Lifecycle()
    {
        string generated = Assert.IsType<string>(Invoke("GenerateLiveSessionWorkflow", "screen", "Send"));

        Assert.Contains("OfferSession", generated, StringComparison.Ordinal);
        Assert.Contains("ActivateSession", generated, StringComparison.Ordinal);
        Assert.Contains("CompleteStopSession", generated, StringComparison.Ordinal);
    }

    private static object? Invoke(string methodName, params object?[] arguments)
        => ToolsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, arguments)
            ?? throw new MissingMethodException(ToolsType.FullName, methodName);
}
