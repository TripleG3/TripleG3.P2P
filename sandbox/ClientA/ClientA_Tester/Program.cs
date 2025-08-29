using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.DependencyInjection;
using TripleG3.P2P.Maui.Attributes;
using System.Net;

var services = new ServiceCollection();
services.AddP2PUdp();
var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<ISerialBus>();

//bus.SubscribeTo<Ping>(p => Console.WriteLine($"<<<< ClientA received: {p.Text} #{p.Count}"));
bus.SubscribeTo<Message>(m => Console.WriteLine($"<<<< {m.Text}"));

await bus.StartListeningAsync(new ProtocolConfiguration
{
	LocalPort = 5001,
	RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5002),
	SerializationProtocol = SerializationProtocol.None
});

//Console.WriteLine("ClientA starting send loop in 1s...");
//await Task.Delay(1000);
//for (int i = 1; i <= 5; i++)
//{
//	await bus.SendAsync(new Ping("Ping from A", i));
//	Console.WriteLine($"ClientA sent #{i}");
//	await Task.Delay(250);
//}
//Console.WriteLine("ClientA done sending. Exiting.");

Console.WriteLine("---- Press P to ping, type a message to send, or Q to quit.");
var choice = Console.ReadLine();

while (choice?.ToUpper() != "Q")
{
	if (choice == null)
		continue;

	if (choice.ToUpper() == "P")
	{
		for (int i = 1; i <= 5; i++)
		{
			await bus.SendAsync(new Ping("Ping from A", i));
			await Task.Delay(250);
		}
	}
	else
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
