using System.Net;
using TripleG3.P2P.Maui.Attributes;
using TripleG3.P2P.Maui.Core;

namespace ClientA_Tester;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("Client A starting...");

        // Delimited (attribute-based) bus
        var busDelimited = SerialBusFactory.CreateUdp();
        await busDelimited.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = 7000,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7001),
            SerializationProtocol = SerializationProtocol.None
        });

        // JSON bus
        var busJson = SerialBusFactory.CreateUdp();
        await busJson.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = 7002,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7003),
            SerializationProtocol = SerializationProtocol.JsonRaw
        });

        SubscribeAll("A-Delimited", busDelimited);
        SubscribeAll("A-Json", busJson);

        // Fire a sequence of sends on both protocols
        for (int i = 0; i < 3; i++)
        {
            await SendAll(busDelimited, $"(Delimited) Iter {i}");
            await SendAll(busJson, $"(Json) Iter {i}");
            await Task.Delay(1500);
        }

        Console.WriteLine("Client A finished sending. Press ENTER to exit.");
        Console.ReadLine();
    }

    private static async Task SendAll(ISerialBus bus, string context)
    {
        // Ping
        await bus.SendAsync(new Ping(DateTime.UtcNow.Ticks));
        Console.WriteLine($"[A][SEND]{context} Ping");
        // Person with nested Address
        var person = new NestedPerson($"PersonA_{context}", 25, new NestedAddress("1 Test Way", "Testville", "TS", "00001"));
        await bus.SendAsync(person);
        Console.WriteLine($"[A][SEND]{context} Person {person.Name}");
        // Simple string
        await bus.SendAsync("Hey from A " + context);
        Console.WriteLine($"[A][SEND]{context} String message");
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
