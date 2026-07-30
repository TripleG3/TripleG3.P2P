using System.Net;
using TripleG3.P2P.FileTransfer;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class FileTransferIntegrationTests
{
    [Fact]
    public async Task Sender_can_transfer_to_an_explicitly_accepted_peer()
    {
        var receiverPort = GetFreePort();
        var senderPort = GetFreePort();
        var source = Path.Combine(Path.GetTempPath(), $"p2p-source-{Guid.NewGuid():N}.bin");
        var destination = Path.Combine(Path.GetTempPath(), $"p2p-destination-{Guid.NewGuid():N}.bin");
        var content = new byte[150_000];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(source, content);

        await using var sender = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, senderPort)
        });
        await using var receiver = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, receiverPort)
        });
        receiver.TransferRequested += (request, _) =>
            new ValueTask<FileTransferDecision>(FileTransferDecision.Accept(destination));

        await receiver.StartAsync();
        await sender.StartAsync();
        var results = await sender.SendAsync(source, [new IPEndPoint(IPAddress.Loopback, receiverPort)]);

        Assert.Single(results);
        Assert.True(results[0].Succeeded, results[0].FailureReason);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));

        File.Delete(source);
        File.Delete(destination);
    }

    [Fact]
    public async Task Receiver_can_reject_a_transfer_without_creating_a_file()
    {
        var receiverPort = GetFreePort();
        var senderPort = GetFreePort();
        var source = Path.Combine(Path.GetTempPath(), $"p2p-reject-{Guid.NewGuid():N}.txt");
        var content = "private content"u8.ToArray();
        await File.WriteAllBytesAsync(source, content);

        await using var sender = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, senderPort)
        });
        await using var receiver = new PeerFileTransferClient(new FileTransferOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, receiverPort)
        });
        receiver.TransferRequested += (request, _) =>
            new ValueTask<FileTransferDecision>(FileTransferDecision.Reject("User declined the transfer."));

        await receiver.StartAsync();
        await sender.StartAsync();
        var results = await sender.SendAsync(source, [new IPEndPoint(IPAddress.Loopback, receiverPort)]);

        Assert.Single(results);
        Assert.False(results[0].Succeeded);
        Assert.Contains("declined", results[0].FailureReason, StringComparison.OrdinalIgnoreCase);

        File.Delete(source);
    }

    [Fact]
    public async Task Receiver_without_a_handler_rejects_by_default()
    {
        var receiverPort = GetFreePort();
        var source = Path.Combine(Path.GetTempPath(), $"p2p-default-reject-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(source, "private content");

        await using var sender = new PeerFileTransferClient(new FileTransferOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, GetFreePort()) });
        await using var receiver = new PeerFileTransferClient(new FileTransferOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, receiverPort) });
        await receiver.StartAsync();
        var results = await sender.SendAsync(source, [new IPEndPoint(IPAddress.Loopback, receiverPort)]);

        Assert.False(Assert.Single(results).Succeeded);
        Assert.Contains("not enabled", results[0].FailureReason, StringComparison.OrdinalIgnoreCase);
        File.Delete(source);
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
