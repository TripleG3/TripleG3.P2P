using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.DependencyInjection;
using TripleG3.P2P.Maui.Attributes;
using System.Net;

var services = new ServiceCollection();
services.AddP2PUdp();
var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<ISerialBus>();

bus.SubscribeTo<Ping>(p => Console.WriteLine($"ClientA received: {p.Text} #{p.Count}"));

await bus.StartListeningAsync(new ProtocolConfiguration
{
	LocalPort = 5001,
	RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5002),
	SerializationProtocol = SerializationProtocol.None
});

Console.WriteLine("ClientA starting send loop in 1s...");
await Task.Delay(1000);
for (int i = 1; i <= 5; i++)
{
	await bus.SendAsync(new Ping("Ping from A", i));
	Console.WriteLine($"ClientA sent #{i}");
	await Task.Delay(250);
}
Console.WriteLine("ClientA done sending. Exiting.");

public class Ping
{
	[Udp(1)] public string Text { get; }
	[Udp(2)] public int Count { get; }
	public Ping(string text, int count) => (Text, Count) = (text, count);
	public override string ToString() => $"{Text}:{Count}";
}
