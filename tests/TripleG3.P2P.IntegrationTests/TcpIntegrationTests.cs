using System.Net;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public class TcpIntegrationTests
{
    [UdpMessage("Chat")] public record Chat([property: Udp(1)] string User, [property: Udp(2)] string Text);
    [UdpMessage("Seq")] public record Seq([property: Udp(1)] int Index);

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Tcp_Basic_Send_And_Receive(SerializationProtocol proto)
    {
        var basePort = PortAllocator.NextBlock();
        var portA = basePort;
        var portB = basePort + 1;

        var a = SerialBusFactory.CreateTcp();
        var b = SerialBusFactory.CreateTcp();

        var receivedA = new List<Chat>();
        var receivedB = new List<Chat>();
        a.SubscribeTo<Chat>(c => { lock(receivedA) receivedA.Add(c); });
        b.SubscribeTo<Chat>(c => { lock(receivedB) receivedB.Add(c); });

        await a.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = portA,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portB),
            SerializationProtocol = proto
        });
        await b.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = portB,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portA),
            SerializationProtocol = proto
        });

        var msg1 = new Chat("A","hello B");
        var msg2 = new Chat("B","hello A");
        await a.SendAsync(msg1);
        await b.SendAsync(msg2);

        await WaitForAsync(() => {
            lock(receivedA) lock(receivedB)
                return receivedA.Any(c => c.Text == msg2.Text) && receivedB.Any(c => c.Text == msg1.Text);
        });

        lock(receivedA) Assert.Contains(receivedA, c => c == msg2);
        lock(receivedB) Assert.Contains(receivedB, c => c == msg1);

        await a.CloseConnectionAsync();
        await b.CloseConnectionAsync();
    }

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Tcp_Broadcast_FanOut(SerializationProtocol proto)
    {
        var basePort = PortAllocator.NextBlock();
        var hubPort = basePort;
        var s1Port = basePort + 1;
        var s2Port = basePort + 2;

        var hub = SerialBusFactory.CreateTcp();
        var s1 = SerialBusFactory.CreateTcp();
        var s2 = SerialBusFactory.CreateTcp();

        var s1Msgs = new List<Chat>();
        var s2Msgs = new List<Chat>();
        s1.SubscribeTo<Chat>(c => { lock(s1Msgs) s1Msgs.Add(c); });
        s2.SubscribeTo<Chat>(c => { lock(s2Msgs) s2Msgs.Add(c); });

        await s1.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = s1Port,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, hubPort),
            SerializationProtocol = proto
        });
        await s2.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = s2Port,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, hubPort),
            SerializationProtocol = proto
        });
        await hub.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = hubPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, s1Port),
            BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, s2Port) },
            SerializationProtocol = proto
        });

        var chat = new Chat("hub","hello spokes");
        await hub.SendAsync(chat);

        await WaitForAsync(() => { lock(s1Msgs) lock(s2Msgs) return s1Msgs.Contains(chat) && s2Msgs.Contains(chat); });

        lock(s1Msgs) Assert.Single(s1Msgs.Where(c => c == chat));
        lock(s2Msgs) Assert.Single(s2Msgs.Where(c => c == chat));

        await hub.CloseConnectionAsync();
        await s1.CloseConnectionAsync();
        await s2.CloseConnectionAsync();
    }

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Tcp_Ordering_Preserved_Per_Peer(SerializationProtocol proto)
    {
        var basePort = PortAllocator.NextBlock();
        var senderPort = basePort;
        var recvPort = basePort + 1;

        var sender = SerialBusFactory.CreateTcp();
        var recv = SerialBusFactory.CreateTcp();
        var seqs = new List<Seq>();
        recv.SubscribeTo<Seq>(s => { lock(seqs) seqs.Add(s); });

        await recv.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = recvPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, senderPort),
            SerializationProtocol = proto
        });
        await sender.StartListeningAsync(new ProtocolConfiguration{
            LocalPort = senderPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, recvPort),
            SerializationProtocol = proto
        });

        const int count = 40;
        for (int i=0;i<count;i++)
            await sender.SendAsync(new Seq(i));

        await WaitForAsync(() => { lock(seqs) return seqs.Count == count; });

        lock(seqs)
        {
            Assert.Equal(count, seqs.Count);
            Assert.True(seqs.Select(s=>s.Index).SequenceEqual(Enumerable.Range(0,count)), "Sequence ordering broken");
        }

        await sender.CloseConnectionAsync();
        await recv.CloseConnectionAsync();
    }

    [Fact]
    public void Ftp_Factory_Not_Implemented()
    {
        Assert.Throws<NotImplementedException>(() => SerialBusFactory.CreateFtp());
    }

    // Helpers
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 4000, int pollMs = 25)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(pollMs);
        }
        Assert.True(condition(), "Condition not met within timeout");
    }

    private static class PortAllocator
    {
        private static int _seed = 25000;
        public static int NextBlock()
        {
            var val = Interlocked.Add(ref _seed, 20);
            if (val > 55000) Interlocked.Exchange(ref _seed, 25000);
            return val;
        }
    }
}
