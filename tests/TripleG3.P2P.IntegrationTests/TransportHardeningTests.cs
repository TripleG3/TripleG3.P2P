using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class TransportHardeningTests
{
    private static int _portSeed = 40000;

    [Fact]
    public async Task Tcp_Concurrent_Sends_Keep_Frames_Intact()
    {
        var basePort = NextPortBlock();
        var sender = SerialBusFactory.CreateTcp();
        var receiver = SerialBusFactory.CreateTcp();
        var received = new List<int>();
        receiver.SubscribeTo<SequenceMessage>(message =>
        {
            lock (received) received.Add(message.Index);
        });

        try
        {
            await receiver.StartListeningAsync(CreateConfig(basePort + 1, basePort));
            await sender.StartListeningAsync(CreateConfig(basePort, basePort + 1));

            var sends = Enumerable.Range(0, 100)
                .Select(index => sender.SendAsync(new SequenceMessage(index)).AsTask());
            await Task.WhenAll(sends);
            await WaitForAsync(() =>
            {
                lock (received) return received.Count == 100;
            });

            lock (received)
            {
                Assert.Equal(Enumerable.Range(0, 100), received.Order());
            }
        }
        finally
        {
            await sender.CloseConnectionAsync();
            await receiver.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Tcp_Unconfigured_Inbound_Client_Does_Not_Receive_FanOut()
    {
        var basePort = NextPortBlock();
        var hub = SerialBusFactory.CreateTcp();
        var configuredPeer = SerialBusFactory.CreateTcp();
        var received = 0;
        configuredPeer.SubscribeTo<SequenceMessage>(_ => Interlocked.Increment(ref received));

        try
        {
            await configuredPeer.StartListeningAsync(CreateConfig(basePort + 1, basePort));
            await hub.StartListeningAsync(CreateConfig(basePort, basePort + 1));
            using var unconfigured = new TcpClient();
            await unconfigured.ConnectAsync(IPAddress.Loopback, basePort);

            await hub.SendAsync(new SequenceMessage(1));
            await WaitForAsync(() => Volatile.Read(ref received) == 1);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var buffer = new byte[1];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await unconfigured.GetStream().ReadAtLeastAsync(buffer, 1, false, timeout.Token));
        }
        finally
        {
            await hub.CloseConnectionAsync();
            await configuredPeer.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Tcp_Reconnects_After_Peer_Restart()
    {
        var basePort = NextPortBlock();
        var sender = SerialBusFactory.CreateTcp();
        var firstReceiver = SerialBusFactory.CreateTcp();
        var firstCount = 0;
        firstReceiver.SubscribeTo<SequenceMessage>(_ => Interlocked.Increment(ref firstCount));

        await firstReceiver.StartListeningAsync(CreateConfig(basePort + 1, basePort));
        await sender.StartListeningAsync(CreateConfig(basePort, basePort + 1));
        await sender.SendAsync(new SequenceMessage(1));
        await WaitForAsync(() => Volatile.Read(ref firstCount) == 1);
        await firstReceiver.CloseConnectionAsync();

        var secondReceiver = SerialBusFactory.CreateTcp();
        var secondCount = 0;
        secondReceiver.SubscribeTo<SequenceMessage>(_ => Interlocked.Increment(ref secondCount));
        try
        {
            await secondReceiver.StartListeningAsync(CreateConfig(basePort + 1, basePort));
            for (var attempt = 0; attempt < 10 && Volatile.Read(ref secondCount) == 0; attempt++)
            {
                try
                {
                    await sender.SendAsync(new SequenceMessage(2));
                }
                catch (AggregateException)
                {
                }

                await Task.Delay(50);
            }

            await WaitForAsync(() => Volatile.Read(ref secondCount) > 0);
        }
        finally
        {
            await sender.CloseConnectionAsync();
            await secondReceiver.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task SendAsync_Propagates_Cancellation()
    {
        var basePort = NextPortBlock();
        var tcp = SerialBusFactory.CreateTcp();
        var udp = SerialBusFactory.CreateUdp();
        try
        {
            await tcp.StartListeningAsync(CreateConfig(basePort, basePort + 1));
            await udp.StartListeningAsync(CreateConfig(basePort + 2, basePort + 3));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tcp.SendAsync(new SequenceMessage(1), cancellationToken: cancellation.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                udp.SendAsync(new SequenceMessage(1), cancellationToken: cancellation.Token).AsTask());
        }
        finally
        {
            await tcp.CloseConnectionAsync();
            await udp.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Udp_Malformed_Frame_Does_Not_Stop_Valid_Delivery()
    {
        var basePort = NextPortBlock();
        var sender = SerialBusFactory.CreateUdp();
        var receiver = SerialBusFactory.CreateUdp();
        var received = 0;
        receiver.SubscribeTo<SequenceMessage>(_ => Interlocked.Increment(ref received));

        try
        {
            await receiver.StartListeningAsync(CreateConfig(basePort + 1, basePort));
            await sender.StartListeningAsync(CreateConfig(basePort, basePort + 1));
            using var rawClient = new UdpClient();
            var malformed = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(malformed, 100);
            await rawClient.SendAsync(malformed, new IPEndPoint(IPAddress.Loopback, basePort + 1));

            await sender.SendAsync(new SequenceMessage(1));
            await WaitForAsync(() => Volatile.Read(ref received) == 1);
        }
        finally
        {
            await sender.CloseConnectionAsync();
            await receiver.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Disposable_Subscription_Unsubscribes_Handler()
    {
        var basePort = NextPortBlock();
        var sender = SerialBusFactory.CreateUdp();
        var receiver = SerialBusFactory.CreateUdp();
        var subscriptionBus = Assert.IsAssignableFrom<ISubscriptionSerialBus>(receiver);
        var received = 0;
        var registration = subscriptionBus.Subscribe<SequenceMessage>(_ => Interlocked.Increment(ref received));

        try
        {
            await receiver.StartListeningAsync(CreateConfig(basePort + 1, basePort));
            await sender.StartListeningAsync(CreateConfig(basePort, basePort + 1));
            await sender.SendAsync(new SequenceMessage(1));
            await WaitForAsync(() => Volatile.Read(ref received) == 1);

            registration.Dispose();
            await sender.SendAsync(new SequenceMessage(2));
            await Task.Delay(150);
            Assert.Equal(1, Volatile.Read(ref received));
        }
        finally
        {
            registration.Dispose();
            await sender.CloseConnectionAsync();
            await receiver.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Disposed_Buses_Cannot_Restart()
    {
        var basePort = NextPortBlock();
        var udp = SerialBusFactory.CreateUdp();
        var tcp = SerialBusFactory.CreateTcp();
        ((IDisposable)udp).Dispose();
        ((IDisposable)tcp).Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            udp.StartListeningAsync(CreateConfig(basePort, basePort + 1)).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            tcp.StartListeningAsync(CreateConfig(basePort + 2, basePort + 3)).AsTask());
    }

    private static ProtocolConfiguration CreateConfig(int localPort, int remotePort)
        => new()
        {
            LocalAddress = IPAddress.Loopback,
            LocalPort = localPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
            SerializationProtocol = SerializationProtocol.LengthPrefixed
        };

    private static int NextPortBlock()
    {
        var value = Interlocked.Add(ref _portSeed, 10);
        if (value <= 60000) return value;
        Interlocked.Exchange(ref _portSeed, 40000);
        return Interlocked.Add(ref _portSeed, 10);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }

    [UdpMessage("HardeningSequence")]
    public sealed record SequenceMessage([property: Udp(1)] int Index);
}