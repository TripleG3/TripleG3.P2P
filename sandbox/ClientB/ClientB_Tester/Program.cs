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
	Console.WriteLine($"ClientB received: {p.Text} #{p.Count}");
	if (p.Count <= 5)
	{
		await bus.SendAsync(new Ping("Pong from B", p.Count));
	}
});

await bus.StartListeningAsync(new ProtocolConfiguration
{
	LocalPort = 5002,
	RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5001),
	SerializationProtocol = SerializationProtocol.None
});

Console.WriteLine("ClientB listening for 5s...");
await Task.Delay(5000);
Console.WriteLine("ClientB exiting.");

public class Ping
{
	[Udp(1)] public string Text { get; }
	[Udp(2)] public int Count { get; }
	public Ping(string text, int count) => (Text, Count) = (text, count);
	public override string ToString() => $"{Text}:{Count}";
}
