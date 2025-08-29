# TripleG3.P2P.Maui

Peer-to-peer UDP messaging abstraction for .NET 9 MAUI apps.

## Features

- Attribute-based contract `[Udp]` with ordered `[Udp(n)]` properties.
- Unified `ISerialBus` interface (future proof for TCP, etc.).
- Minimal header (8 bytes) + payload.
- Pluggable serialization strategies (None / JsonRaw).
- High-performance span-based header parsing & custom delimiter `@-@`.

## Usage

```csharp
builder.Services.AddP2PUdp();
```

```csharp
[Udp]
public record Person([Udp(1)] string Name, [Udp(2)] int Age);

var bus = provider.GetRequiredService<ISerialBus>();
await bus.StartListeningAsync(new ProtocolConfiguration
{
    LocalPort = 9000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9000),
    SerializationProtocol = SerializationProtocol.None
});

bus.SubscribeTo<Person>(p => Console.WriteLine($"Person: {p.Name} {p.Age}"));
await bus.SendAsync(new Person("Jane", 30));
```

## Header

| Bytes | Meaning |
|-------|---------|
|0-3|Payload Length (excluding header)|
|4-5|MessageType|
|6-7|SerializationProtocol|

## Roadmap

- TCP implementation
- Encrypted protocol option
- Source generator for faster serialization
- Binary packing for primitive types
