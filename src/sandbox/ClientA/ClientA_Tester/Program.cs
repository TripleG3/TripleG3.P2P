using System.Net;
using TripleG3.P2P.Maui.Attributes;
using TripleG3.P2P.Maui.Core;

namespace ClientA_Tester;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("Client A starting...");

        var bus = SerialBusFactory.CreateUdp();
        await bus.StartListeningAsync(new ProtocolConfiguration
        {
            LocalPort = 6000,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6001),
            SerializationProtocol = SerializationProtocol.None
        });

        bus.SubscribeTo<NestedPerson>(p =>
        {
            Console.WriteLine($"[A] Received Person: {p.Name}, Age {p.Age}, Address: {p.Address.Street} {p.Address.City} {p.Address.State} {p.Address.Zip}");
        });

        for (int i = 0; i < 5; i++)
        {
            var person = new NestedPerson(
                $"PersonA_{i}",
                20 + i,
                new NestedAddress($"{i} Main St", "Townsville", "TS", $"000{i}")
            );
            await bus.SendAsync(person);
            Console.WriteLine($"[A] Sent Person {person.Name}");
            await Task.Delay(1500);
        }

        Console.WriteLine("Client A done. Press ENTER to exit.");
        Console.ReadLine();
    }
}

[UdpMessage("Person")] // Protocol name
public record NestedPerson([property: Udp(1)] string Name, [property: Udp(2)] int Age, [property: Udp(3)] NestedAddress Address)
{
    public static NestedPerson Empty { get; } = new(string.Empty, 0, NestedAddress.Empty);
}

[UdpMessage("Address")] // Explicit protocol name
public record NestedAddress([property: Udp(1)] string Street, [property: Udp(2)] string City, [property: Udp(3)] string State, [property: Udp(4)] string Zip)
{
    public static NestedAddress Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
