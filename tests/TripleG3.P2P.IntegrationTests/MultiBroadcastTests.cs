using System.Net;
using System.Linq;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public class MultiBroadcastTests
{
    [UdpMessage("Chat")] public record Chat([property: Udp(1)] string User, [property: Udp(2)] string Text);

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Broadcast_Message_Reaches_All_Peers(SerializationProtocol proto)
    {
        // Arrange three peers (A sends to B + C). Unique ports chosen from dynamic base.
        var basePort = GetEphemeralBasePort();
        var portA = basePort;
        var portB = basePort + 1;
        var portC = basePort + 2;

        var busA = SerialBusFactory.CreateUdp();
        var busB = SerialBusFactory.CreateUdp();
        var busC = SerialBusFactory.CreateUdp();

        var receivedB = new List<Chat>();
        var receivedC = new List<Chat>();

    busB.SubscribeTo<Chat>(c => { lock (receivedB) receivedB.Add(c); });
    busC.SubscribeTo<Chat>(c => { lock (receivedC) receivedC.Add(c); });

        await busB.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portB,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portA),
            SerializationProtocol = proto
        });
        await busC.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portC,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portA),
            SerializationProtocol = proto
        });
        await busA.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portA,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portB), // primary
            BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, portC) },
            SerializationProtocol = proto
        });

        // Act
        var msg = new Chat("alice", "hello all");
        await busA.SendAsync(msg);

        // Allow network propagation
        await WaitForAsync(() => {
            lock(receivedB) lock(receivedC)
                return receivedB.Any(c => c == msg) && receivedC.Any(c => c == msg);
        });

        // Assert
        lock(receivedB) Assert.Contains(receivedB, c => c == msg);
        lock(receivedC) Assert.Contains(receivedC, c => c == msg);

        await busA.CloseConnectionAsync();
        await busB.CloseConnectionAsync();
        await busC.CloseConnectionAsync();
    }

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Each_Peer_Can_Send_Back_To_Sender(SerializationProtocol proto)
    {
        // Arrange star topology: central hub (H) listens; two spokes (S1,S2) broadcast to hub only; hub broadcasts to both.
        var basePort = GetEphemeralBasePort();
        var portHub = basePort;
        var portS1 = basePort + 1;
        var portS2 = basePort + 2;

        var hub = SerialBusFactory.CreateUdp();
        var s1 = SerialBusFactory.CreateUdp();
        var s2 = SerialBusFactory.CreateUdp();

        var hubReceived = new List<Chat>();
        var s1Received = new List<Chat>();
        var s2Received = new List<Chat>();

    hub.SubscribeTo<Chat>(c => { lock (hubReceived) hubReceived.Add(c); });
    s1.SubscribeTo<Chat>(c => { lock (s1Received) s1Received.Add(c); });
    s2.SubscribeTo<Chat>(c => { lock (s2Received) s2Received.Add(c); });

        await hub.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portHub,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portS1),
            BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, portS2) },
            SerializationProtocol = proto
        });
        await s1.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portS1,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portHub),
            SerializationProtocol = proto
        });
        await s2.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portS2,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portHub),
            SerializationProtocol = proto
        });

        // Act: spokes send messages to hub
        var m1 = new Chat("s1", "from s1");
        var m2 = new Chat("s2", "from s2");
        await s1.SendAsync(m1);
        await s2.SendAsync(m2);

        // Hub broadcasts reply
        var reply = new Chat("hub", "ack all");
        await hub.SendAsync(reply);

        await WaitForAsync(() => {
            lock(hubReceived) lock(s1Received) lock(s2Received)
                return hubReceived.Contains(m1) && hubReceived.Contains(m2) && s1Received.Contains(reply) && s2Received.Contains(reply);
        });

        // Assert hub received both spoke messages
        lock(hubReceived)
        {
            Assert.Contains(hubReceived, c => c == m1);
            Assert.Contains(hubReceived, c => c == m2);
        }
        // Assert each spoke received the hub broadcast
        lock(s1Received) Assert.Contains(s1Received, c => c == reply);
        lock(s2Received) Assert.Contains(s2Received, c => c == reply);

        await hub.CloseConnectionAsync();
        await s1.CloseConnectionAsync();
        await s2.CloseConnectionAsync();
    }

    private static int _portSeed = 12000; // start in mid-range to avoid privileged & ephemeral conflicts
    private static int GetEphemeralBasePort()
    {
        // Reserve a block of 10 ports per allocation; wrap if approaching upper range
        int next = System.Threading.Interlocked.Add(ref _portSeed, 10);
        if (next > 20000)
        {
            // reset (best effort)
            _portSeed = 12000;
            next = System.Threading.Interlocked.Add(ref _portSeed, 10);
        }
        return next;
    }

    [UdpMessage("Person")] public record Person([property: Udp(1)] string Name, [property: Udp(2)] int Age, [property: Udp(3)] Address Address);
    [UdpMessage("Address")] public record Address([property: Udp(1)] string Street, [property: Udp(2)] string City);

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Broadcast_With_Duplicate_Endpoints_Does_Not_Double_Send(SerializationProtocol proto)
    {
        var basePort = GetEphemeralBasePort();
        var portSender = basePort;
        var portR1 = basePort + 1;
        var portR2 = basePort + 2;

        var sender = SerialBusFactory.CreateUdp();
        var r1 = SerialBusFactory.CreateUdp();
        var r2 = SerialBusFactory.CreateUdp();

        var r1Msgs = new List<string>();
        var r2Persons = new List<Person>();
        r1.SubscribeTo<string>(s => { lock (r1Msgs) r1Msgs.Add(s); });
        r2.SubscribeTo<Person>(p => { lock (r2Persons) r2Persons.Add(p); });

        await r1.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portR1,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portSender),
            SerializationProtocol = proto
        });
        await r2.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portR2,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portSender),
            SerializationProtocol = proto
        });
        // Duplicate endpoints (portR2 repeated) + primary RemoteEndPoint covers portR1
        await sender.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = portSender,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, portR1),
            BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, portR2), new IPEndPoint(IPAddress.Loopback, portR2) },
            SerializationProtocol = proto
        });

        await sender.SendAsync("hello");
        var person = new Person("bob", 33, new Address("1 St","Town"));
        await sender.SendAsync(person);
        await WaitForAsync(() => {
            lock(r1Msgs) lock(r2Persons)
                return r1Msgs.Count(m => m == "hello") == 1 && r2Persons.Any(p => p.Name == "bob");
        });

        lock (r1Msgs) Assert.Equal(1, r1Msgs.Count(m => m == "hello"));
        lock (r2Persons)
        {
            Assert.Equal(1, r2Persons.Count(p => p.Name == person.Name && p.Age == person.Age && p.Address.City == person.Address.City));
        }

        await sender.CloseConnectionAsync();
        await r1.CloseConnectionAsync();
        await r2.CloseConnectionAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000, int pollMs = 25)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(pollMs);
        }
        Assert.True(condition(), "Condition not met within timeout");
    }

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Concurrent_Broadcasts_All_Messages_Delivered_Exactly_Once(SerializationProtocol proto)
    {
        // 4 peers each broadcasting to the other 3
        var basePort = GetEphemeralBasePort();
        int peerCount = 4;
        var ports = Enumerable.Range(basePort, peerCount).ToArray();
        var buses = new ISerialBus[peerCount];
        var received = new List<Chat>[peerCount];

        for (int i = 0; i < peerCount; i++)
        {
            buses[i] = SerialBusFactory.CreateUdp();
            received[i] = new List<Chat>();
            int capture = i;
            buses[i].SubscribeTo<Chat>(c => { lock(received[capture]) received[capture].Add(c); });
        }

        // Start all
        for (int i = 0; i < peerCount; i++)
        {
            var others = Enumerable.Range(0, peerCount).Where(x => x != i).ToArray();
            var remote = new IPEndPoint(IPAddress.Loopback, ports[others[0]]);
            var broadcasts = others.Skip(1).Select(x => new IPEndPoint(IPAddress.Loopback, ports[x])).ToArray();
            await buses[i].StartListeningAsync(new ProtocolConfiguration
            {
                LocalPort = ports[i],
                RemoteEndPoint = remote,
                BroadcastEndPoints = broadcasts,
                SerializationProtocol = proto
            });
        }

        // Each peer sends M messages
        int messagesPerPeer = 5;
        var sendTasks = new List<Task>();
        for (int i = 0; i < peerCount; i++)
        {
            int capture = i;
            sendTasks.Add(Task.Run(async () =>
            {
                for (int m = 0; m < messagesPerPeer; m++)
                {
                    await buses[capture].SendAsync(new Chat($"peer{capture}", $"msg{m}"));
                    await Task.Delay(5); // slight stagger
                }
            }));
        }
        await Task.WhenAll(sendTasks);

        int expectedPerPeer = (peerCount - 1) * messagesPerPeer; // each peer receives others' messages
        await WaitForAsync(() => received.All(list => { lock(list) return list.Count >= expectedPerPeer; }));

        // Validate no duplicates beyond expected counts & message integrity
        for (int i = 0; i < peerCount; i++)
        {
            lock (received[i])
            {
                Assert.Equal(expectedPerPeer, received[i].Count);
                // Group by (User, Text)
                var dup = received[i].GroupBy(c => (c.User, c.Text)).Where(g => g.Count() > 1).ToList();
                Assert.True(dup.Count == 0, $"Peer {i} has duplicate messages: {string.Join(", ", dup.Select(d => d.Key))}");
            }
        }

        foreach (var b in buses) await b.CloseConnectionAsync();
    }

    [Theory]
    [InlineData(SerializationProtocol.None)]
    [InlineData(SerializationProtocol.JsonRaw)]
    public async Task Mixed_Message_Types_Broadcast_Correctly(SerializationProtocol proto)
    {
        var basePort = GetEphemeralBasePort();
        var senderPort = basePort;
        var recv1Port = basePort + 1;
        var recv2Port = basePort + 2;

        var sender = SerialBusFactory.CreateUdp();
        var r1 = SerialBusFactory.CreateUdp();
        var r2 = SerialBusFactory.CreateUdp();

        var strings1 = new List<string>();
        var strings2 = new List<string>();
        var persons1 = new List<Person>();
        var persons2 = new List<Person>();

        r1.SubscribeTo<string>(s => { lock(strings1) strings1.Add(s); });
        r2.SubscribeTo<string>(s => { lock(strings2) strings2.Add(s); });
        r1.SubscribeTo<Person>(p => { lock(persons1) persons1.Add(p); });
        r2.SubscribeTo<Person>(p => { lock(persons2) persons2.Add(p); });

        await r1.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = recv1Port,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, senderPort),
            SerializationProtocol = proto
        });
        await r2.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = recv2Port,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, senderPort),
            SerializationProtocol = proto
        });
        await sender.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = senderPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, recv1Port),
            BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, recv2Port) },
            SerializationProtocol = proto
        });

        await sender.SendAsync("alpha");
        await sender.SendAsync(new Person("carol", 27, new Address("2 Ave", "Metro")));
        await sender.SendAsync("beta");
        await sender.SendAsync(new Person("dave", 41, new Address("3 Blvd", "Metro")));

        await WaitForAsync(() =>
        {
            lock(strings1) lock(strings2) lock(persons1) lock(persons2)
                return strings1.Count >= 2 && strings2.Count >= 2 && persons1.Count >= 2 && persons2.Count >= 2;
        });

        string[] expectedStrings = ["alpha", "beta"]; string[] expectedPersons = ["carol", "dave"];
        lock(strings1) Assert.True(expectedStrings.All(s => strings1.Contains(s)));
        lock(strings2) Assert.True(expectedStrings.All(s => strings2.Contains(s)));
        lock(persons1) Assert.True(expectedPersons.All(n => persons1.Any(p => p.Name == n)));
        lock(persons2) Assert.True(expectedPersons.All(n => persons2.Any(p => p.Name == n)));

        await sender.CloseConnectionAsync();
        await r1.CloseConnectionAsync();
        await r2.CloseConnectionAsync();
    }
}
