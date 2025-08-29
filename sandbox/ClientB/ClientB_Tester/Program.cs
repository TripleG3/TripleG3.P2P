using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.DependencyInjection;
using TripleG3.P2P.Maui.Attributes;
using System.Net;

var services = new ServiceCollection();
services.AddP2PUdp();
var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<ISerialBus>();

bus.SubscribeTo<Ping>(async p =>
{
	Console.WriteLine($"<<<< ClientB received: {p.Text} #{p.Count}");
	if (p.Count <= 50)
	{
		await bus.SendAsync(new Ping("Pong from B", p.Count));
	}
});
bus.SubscribeTo<Message>(m => Console.WriteLine($"<<<< {m.Text}"));

await bus.StartListeningAsync(new ProtocolConfiguration
{
	LocalPort = 5002,
	RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5001),
	SerializationProtocol = SerializationProtocol.None
});

//Console.WriteLine("ClientB listening for 5s...");
//await Task.Delay(5000);
//Console.WriteLine("ClientB exiting.");
var choice = Console.ReadLine();

while (choice?.ToUpper() != "Q")
{
	if (choice?.ToUpper() == "P")
	{
		await bus.SendAsync(new Ping("Ping from B", 1));
	}
	else if (!string.IsNullOrWhiteSpace(choice))
	{
		await bus.SendAsync(new Message(choice));
	}
	Console.WriteLine("---- Press P to ping, type a message to send, or Q to quit.");
	choice = Console.ReadLine();
}

Console.Read();

public record Ping([property: Udp(1)] string Text, [property: Udp(2)] int Count)
{
    public static Ping Empty { get; } = new Ping(string.Empty, 0);
    public override string ToString() => $"{Text}:{Count}";
}

public record Message([property: Udp(1)] string Text)
{
    public static Message Empty { get; } = new Message(string.Empty);
    public override string ToString() => $"{Text}";
}
