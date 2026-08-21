using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TripleG3.P2P.Transfer;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class PeerTransferSessionIntegrationTests
{
    [Fact]
    public async Task Session_cancels_one_transfer_without_closing_another_transfer_or_the_session()
    {
        Guid sessionId = Guid.NewGuid();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        PeerTransferSessionFactory factory = new();
        PeerTransferSessionOptions receiverOptions = CreateOptions(sessionId, "device-b", "device-a");
        Task<IPeerTransferSession> accepting = AcceptAsync(listener, factory, receiverOptions);
        await using IPeerTransferSession sender = await factory.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port),
            CreateOptions(sessionId, "device-a", "device-b"));
        await using IPeerTransferSession receiver = await accepting;

        TaskCompletionSource<IPeerTransfer> firstInboundOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IPeerTransfer> secondInboundOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Guid> firstInboundCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentDictionary<Guid, byte[]> receivedPayloads = [];
        ConcurrentDictionary<Guid, Task> consumers = [];
        receiver.TransferRequested += (_, _) => ValueTask.FromResult(PeerTransferDecision.Accept());
        receiver.InboundTransferOpened += transfer =>
        {
            if (string.Equals(transfer.Snapshot.DisplayName, "first", StringComparison.Ordinal))
            {
                firstInboundOpened.TrySetResult(transfer);
            }
            else if (string.Equals(transfer.Snapshot.DisplayName, "second", StringComparison.Ordinal))
            {
                secondInboundOpened.TrySetResult(transfer);
            }

            consumers[transfer.Snapshot.TransferId] = ConsumeAsync(transfer, receivedPayloads, firstInboundCancelled);
        };

        IPeerTransfer first = await sender.OpenTransferAsync(CreateDescriptor("first", 8));
        await firstInboundOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.SendAsync(Encoding.UTF8.GetBytes("partial"));
        await first.CancelAsync("The first transfer is no longer needed.");
        await firstInboundCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        byte[] expected = Encoding.UTF8.GetBytes("The second transfer remains healthy.");
        IPeerTransfer second = await sender.OpenTransferAsync(CreateDescriptor("second", expected.Length));
        await secondInboundOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.SendAsync(expected);
        await second.CompleteAsync();

        Assert.True(sender.TryGetTransfer(first.Snapshot.TransferId, out PeerTransferSnapshot firstSender));
        Assert.True(receiver.TryGetTransfer(first.Snapshot.TransferId, out PeerTransferSnapshot firstReceiver));
        Assert.Equal(PeerTransferState.Cancelled, firstSender.State);
        Assert.Equal(PeerTransferState.Cancelled, firstReceiver.State);
        Assert.True(sender.TryGetTransfer(second.Snapshot.TransferId, out PeerTransferSnapshot secondSender));
        Assert.True(receiver.TryGetTransfer(second.Snapshot.TransferId, out PeerTransferSnapshot secondReceiver));
        Assert.Equal(PeerTransferState.Completed, secondSender.State);
        Assert.Equal(PeerTransferState.Completed, secondReceiver.State);
        Assert.Equal(expected, receivedPayloads[second.Snapshot.TransferId]);
        Assert.Equal(PeerTransferSessionState.Connected, sender.Snapshot.State);
        Assert.Equal(PeerTransferSessionState.Connected, receiver.Snapshot.State);

        await Task.WhenAll(consumers.Values);
    }

    private static PeerTransferSessionOptions CreateOptions(Guid sessionId, string localDeviceId, string remoteDeviceId) =>
        new(sessionId, localDeviceId, remoteDeviceId, "integration-test-grant")
        {
            Authorizer = new AllowAllAuthorizer(),
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            ControlTimeout = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            SessionInactivityTimeout = TimeSpan.FromMinutes(2)
        };

    private static PeerTransferDescriptor CreateDescriptor(string displayName, long expectedLength) =>
        new(Guid.NewGuid(), PeerTransferKind.RawText, displayName, $"Transfer {displayName}", expectedLength, string.Empty);

    private static async Task<IPeerTransferSession> AcceptAsync(
        TcpListener listener,
        IPeerTransferSessionFactory factory,
        PeerTransferSessionOptions options)
    {
        TcpClient client = await listener.AcceptTcpClientAsync();
        return await factory.AcceptAsync(client, options);
    }

    private static async Task ConsumeAsync(
        IPeerTransfer transfer,
        ConcurrentDictionary<Guid, byte[]> receivedPayloads,
        TaskCompletionSource<Guid> cancelled)
    {
        using MemoryStream memory = new();
        try
        {
            await foreach (ReadOnlyMemory<byte> payload in transfer.ReadAllAsync())
            {
                await memory.WriteAsync(payload);
            }

            receivedPayloads[transfer.Snapshot.TransferId] = memory.ToArray();
            await transfer.CompleteAsync();
        }
        catch (OperationCanceledException)
        {
            cancelled.TrySetResult(transfer.Snapshot.TransferId);
        }
    }

    private sealed class AllowAllAuthorizer : IPeerTransferSessionAuthorizer
    {
        public ValueTask<PeerTransferSessionAuthorization> AuthorizeAsync(
            PeerTransferSessionHello hello,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PeerTransferSessionAuthorization.Allow());
    }
}