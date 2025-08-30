using System.Net;
using TripleG3.P2P.Maui.Attributes;
using TripleG3.P2P.Maui.Core;

namespace ClientB_Tester;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("Client B starting...");

        // Delimited (attribute-based) bus
        var busDelimited = SerialBusFactory.CreateUdp();
        await busDelimited.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = 7001,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7000),
            SerializationProtocol = SerializationProtocol.None
        });

        // JSON bus
        var busJson = SerialBusFactory.CreateUdp();
        await busJson.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = 7003,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7002),
            SerializationProtocol = SerializationProtocol.JsonRaw
        });

        SubscribeAll("B-Delimited", busDelimited);
        SubscribeAll("B-Json", busJson);

        await Task.Delay(1000); // ensure A is likely started first

        for (int i = 0; i < 3; i++)
        {
            await SendAll(busDelimited);
            await SendAll(busJson);
        }

        Console.WriteLine("Client B finished sending. Press ENTER to exit.");
        Console.ReadLine();
    }

    private static async Task SendAll(ISerialBus bus)
    {
        await bus.SendAsync(new Ping(DateTime.UtcNow.Ticks));
        //Console.WriteLine($"[B][SEND]{context} Ping");
        var person = new NestedPerson($"PersonB", 30, new NestedAddress("2 Example Rd", "Exampletown", "EX", "99999"));
        await bus.SendAsync(person);
        //Console.WriteLine($"[B][SEND]{context} Person {person.Name}");
        await bus.SendAsync("Hey from B ");
        //Console.WriteLine($"[B][SEND]{context} String message");
    }

    private static void SubscribeAll(string tag, ISerialBus bus)
    {
        bus.SubscribeTo<Ping>(p =>
        {
            var rttMs = (DateTime.UtcNow.Ticks - p.Ticks) / TimeSpan.TicksPerMillisecond;
            Console.WriteLine($"[{tag}] Ping received. Age={rttMs}ms");
        });
        bus.SubscribeTo<NestedPerson>(p =>
        {
            Console.WriteLine($"[{tag}] Person: {p.Name} Age={p.Age} City={p.Address.City}");
        });
        bus.SubscribeTo<string>(s =>
        {
            Console.WriteLine($"[{tag}] String: {s}");
        });
    }
}

[UdpMessage("Ping")]
public record Ping([property: Udp(1)] long Ticks);

[UdpMessage("Person")]
public record NestedPerson([property: Udp(1)] string Name, [property: Udp(2)] int Age, [property: Udp(3)] NestedAddress Address)
{
    public static NestedPerson Empty { get; } = new(string.Empty, 0, NestedAddress.Empty);
}

[UdpMessage("Address")]
public record NestedAddress([property: Udp(1)] string Street, [property: Udp(2)] string City, [property: Udp(3)] string State, [property: Udp(4)] string Zip)
{
    public static NestedAddress Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
