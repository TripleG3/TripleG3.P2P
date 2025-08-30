# TripleG3.P2P

High-performance, attribute-driven peer-to-peer messaging for .NET 9 / MAUI apps over UDP (extensible to future TCP / other transports). Ship strongly-typed messages (records / classes / primitives / strings) with a tiny 8‑byte header + pluggable serialization strategy.

> Status: UDP transport + two serializers (Delimited / `None` and `JsonRaw`) implemented. Designed so additional transports (TCP, etc.) and serializers can slot in without breaking user code.

---

## Why?

Typical networking layers force you to hand-roll framing, routing, and serialization. TripleG3.P2P gives you:

- A single minimal interface: `ISerialBus` (send, subscribe, start, close)
- Deterministic wire contract via `[Udp]` & `[UdpMessage]` attributes (order + protocol name stability)
- Envelope-based dispatch that is assembly agnostic (type *names* / attribute names, not CLR identity)
- Choice between ultra-light delimiter serialization or raw JSON
- Safe, isolated subscriptions (late subscribers don't crash the loop)
- Zero allocations for header parsing (Span/Memory friendly design internally)
- NEW: Multi-endpoint broadcast fan-out with duplicate endpoint suppression

---

## Features At A Glance

- Multi-target (net9.0 + net9.0-android + net9.0-ios + net9.0-maccatalyst + conditional windows)
- 8‑byte header layout (Length / MessageType / SerializationProtocol)
- Attribute ordered property serialization with stable delimiter `@-@`
- Automatic Envelope wrapping so handlers receive strong types directly
- Multiple simultaneous protocol instances (e.g. Delimited + JSON in sample)
- Multi-endpoint broadcast (one `SendAsync` -> N peers)
- Plug-in serializer model (`IMessageSerializer`)
- Graceful cancellation / disposal
- NEW: TCP transport (reliable stream) alongside UDP; same API
- FTP transport placeholder (factory method exists; not implemented yet)

### Transport Summary

| Transport | Reliability | Ordering | Broadcast Fan-Out | Status |
|-----------|-------------|----------|-------------------|--------|
| UDP       | Best effort / loss possible | Not guaranteed across datagrams | Yes (multi-endpoint send) | Implemented |
| TCP       | Reliable | Preserved per connection | Yes (writes to each stream) | Implemented |
| FTP       | N/A (file transfer layer) | N/A | Planned (large payload offload) | Planned |

Use UDP when you need lowest overhead and can tolerate loss; use TCP when you need reliability / ordering without building it yourself.

---

## Installation

NuGet (once published):

```bash
dotnet add package TripleG3.P2P
```

Or reference the project directly while developing.

---

## Target Frameworks

Current `TargetFrameworks`:

```text
net9.0
```

Pick the one you consume in your app; NuGet tooling selects the right asset.

---

## Core Concepts

### ISerialBus

```csharp
public interface ISerialBus {
    bool IsListening { get; }
    ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken ct = default);
    ValueTask CloseConnectionAsync();
    void SubscribeTo<T>(Action<T> handler);
    ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken ct = default);
}
```
Abstracts the transport (currently UDP + TCP; FTP planned). Your code remains identical besides construction via the factory.

### ProtocolConfiguration

```csharp
public sealed class ProtocolConfiguration {
    IPEndPoint RemoteEndPoint { get; init; }
    IReadOnlyCollection<IPEndPoint> BroadcastEndPoints { get; init; }
    int        LocalPort      { get; init; }
    SerializationProtocol SerializationProtocol { get; init; }
}
```

Controls binding + outbound destination and the serialization protocol used for every message on this bus instance.

#### Broadcasting / Fan-Out

Provide one or more `BroadcastEndPoints` to automatically fan out every `SendAsync` from a bus instance to: `RemoteEndPoint ∪ BroadcastEndPoints` (set semantics). Duplicate endpoints (same `address:port`) are suppressed.

Basic one-to-many:

```csharp
await bus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 7000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7001), // primary
    BroadcastEndPoints = new [] {
        new IPEndPoint(IPAddress.Loopback, 7002),
        new IPEndPoint(IPAddress.Loopback, 7003)
    },
    SerializationProtocol = SerializationProtocol.None
});

await bus.SendAsync(new Chat("me", "hi everyone")); // reaches 7001, 7002, 7003
```

Hub & Spokes (hub at 8000 -> spokes 8001/8002, spokes reply only to hub):

```csharp
// Hub
await hub.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 8000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8001),
    BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, 8002) },
    SerializationProtocol = SerializationProtocol.JsonRaw
});

// Spoke 1
await s1.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 8001,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8000),
    SerializationProtocol = SerializationProtocol.JsonRaw
});

// Spoke 2 (similar to s1; LocalPort = 8002)

await hub.SendAsync(new BroadcastAnnouncement("server", "hello spokes"));
```

Full Mesh (N peers each send to all others): create N buses where for each index `i` choose one peer as `RemoteEndPoint` and all remaining as `BroadcastEndPoints`. (See integration test `Concurrent_Broadcasts_All_Messages_Delivered_Exactly_Once`).

Duplicate endpoint suppression:

```csharp
await bus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 8100,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 8101),
    BroadcastEndPoints = new [] { // 8102 duplicated
        new IPEndPoint(IPAddress.Loopback, 8102),
        new IPEndPoint(IPAddress.Loopback, 8102)
    }
});
// Only one datagram sent to 8102.
```

Mixed types broadcast (string + complex):

```csharp
await sender.SendAsync("alpha");
await sender.SendAsync(new Person("carol", 27, new Address("2 Ave", "Metro")));
```

Concurrency: safe to invoke `SendAsync` concurrently; the underlying UDP socket will serialize sends. Out-of-order arrival is still possible (UDP). If ordering matters, include sequence numbers inside your messages.

Reliability Disclaimer: UDP does not guarantee delivery / ordering. The library only guarantees *attempted* one-shot fan-out; implement retries / ACK at the message layer if required.

### Envelope (Generic)

Internal transport wrapper: `TypeName` + `Message`. The receiver inspects `TypeName` to look up subscriptions, then materializes only the requested type.

### SerializationProtocol

```text
None    // Attribute-delimited (fast, compact)
JsonRaw // System.Text.Json UTF-8 payload
```
Add more by implementing `IMessageSerializer`.

### Attributes

- `[UdpMessage]` or `[UdpMessage("CustomName")]` gives the logical protocol name (stable across assemblies)
- `[UdpMessage<T>]` generic variant uses `typeof(T).Name` (or supplied override) for convenience
- `[Udp(order)]` marks & orders properties participating in delimiter serialization
    - Unannotated properties are ignored by the `None` serializer

### MessageType

Currently: `Data` (extensible placeholder for control, ack, etc.)

---
## Wire Format (UDP)

Header (8 bytes total):

1. Bytes 0-3: Int32 PayloadLength (bytes after header)
2. Bytes 4-5: Int16 MessageType
3. Bytes 6-7: Int16 SerializationProtocol

Payload:

- If `SerializationProtocol.None`: `TypeName` + optional `@-@` + serialized property segments (each delimited by `@-@`)
- If `JsonRaw`: UTF-8 JSON of the `Envelope<T>`

---
## Quick Start


```csharp
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using System.Net;

[UdpMessage("Person")] // Protocol type name
public record Person([property: Udp(1)] string Name,
                     [property: Udp(2)] int Age,
                     [property: Udp(3)] Address Address);

[UdpMessage<Address>] // Uses nameof(Address) unless overridden
public record Address([property: Udp(1)] string Street,
                      [property: Udp(2)] string City,
                      [property: Udp(3)] string State,
                      [property: Udp(4)] string Zip);

var bus = SerialBusFactory.CreateUdp();
await bus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 7000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7001),
    SerializationProtocol = SerializationProtocol.None
});

bus.SubscribeTo<Person>(p => Console.WriteLine($"Person: {p.Name} ({p.Age}) {p.Address.City}"));

await bus.SendAsync(new Person("Alice", 28, new Address("1 Way", "Town", "ST", "00001")));
```

Run a second process with reversed ports (7001 <-> 7000) to complete the loop.

### TCP Quick Start (Reliable)

```csharp
var tcpBus = SerialBusFactory.CreateTcp();
await tcpBus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 9000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9001),
    SerializationProtocol = SerializationProtocol.JsonRaw
});

tcpBus.SubscribeTo<Person>(p => Console.WriteLine($"[TCP] {p.Name} ({p.Age})"));
await tcpBus.SendAsync(new Person("Alice", 28, new Address("1 Way", "Town", "ST", "00001")));
```

### Multi-Protocol Usage (Same Message Types)

```csharp
var udp = SerialBusFactory.CreateUdp();
var tcp = SerialBusFactory.CreateTcp();
await udp.StartListeningAsync(new ProtocolConfiguration { LocalPort = 7000, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7001), SerializationProtocol = SerializationProtocol.None });
await tcp.StartListeningAsync(new ProtocolConfiguration { LocalPort = 9000, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9001), SerializationProtocol = SerializationProtocol.JsonRaw });

udp.SubscribeTo<Chat>(c => Console.WriteLine($"[UDP] {c.User}: {c.Text}"));
tcp.SubscribeTo<Chat>(c => Console.WriteLine($"[TCP] {c.User}: {c.Text}"));

await udp.SendAsync(new Chat("me","low-latency"));
await tcp.SendAsync(new Chat("me","reliable"));
```

---
## Using JSON Instead


```csharp
var jsonBus = SerialBusFactory.CreateUdp();
await jsonBus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 7002,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7003),
    SerializationProtocol = SerializationProtocol.JsonRaw
});
```

JSON serializer ignores `[Udp]` ordering—standard JSON rules apply; `TypeName` embedded as `typeName`/`TypeName`.

---
## Subscriptions


```csharp
bus.SubscribeTo<string>(s => Console.WriteLine($"Raw string: {s}"));
bus.SubscribeTo<Person>(HandlePerson);

void HandlePerson(Person p) { /*...*/ }
```

- Multiple handlers per type allowed
- Subscription key is the protocol type name (attribute override or CLR name)
- If no handler matches, message is silently ignored

---
## Sending


```csharp
await bus.SendAsync("Hello peer");
await bus.SendAsync(new Person("Bob", 42, new Address("2 Road", "City", "ST", "22222")));
```

All messages on a bus instance use that instance’s `SerializationProtocol`.

---
## Graceful Shutdown


```csharp
await bus.CloseConnectionAsync();
// or dispose
(bus as IDisposable)?.Dispose();
```

Cancels the receive loop & disposes socket.

---
## Designing Message Contracts (Delimited Serializer)

1. Add `[UdpMessage]` (optional if CLR name is acceptable) to each root message type.
2. Annotate properties you want serialized with `[Udp(order)]` (1-based ordering recommended).
3. Use only deterministic, immutable shapes (records ideal).
4. Nested complex types must also follow the same attribute pattern.
5. Changing order or adding/removing annotated properties is a protocol breaking change.

### Example

```csharp
[UdpMessage("Ping")] public record Ping([property: Udp(1)] long Ticks);
```

### Primitive & String Support

Primitive-like types (numeric, enum, Guid, DateTime, DateTimeOffset, decimal, string) are converted with `ToString()` / parsed at receive time.

---
## Implementing a Custom Serializer


```csharp
class MyBinarySerializer : IMessageSerializer {
    public SerializationProtocol Protocol => (SerializationProtocol)42; // Add new enum value first
    public byte[] Serialize<T>(T value) { /* return bytes */ }
    public T? Deserialize<T>(ReadOnlySpan<byte> data) { /* parse */ }
    public object? Deserialize(Type t, ReadOnlySpan<byte> data) { /* parse */ }
}
```
Register by supplying it to `SerialBusFactory` (extend or create your own factory method mirroring the built-in one).

---
## Error Handling & Resilience

- Receive loop swallows unexpected exceptions to keep the socket alive (add logging hook where `catch { }` blocks exist if needed)
- Malformed messages are skipped
- Individual subscriber exceptions do not block other handlers

---
## Performance Notes

- Header parsing uses `BinaryPrimitives` on a single span
- Delimited serializer caches reflection lookups per type
- No dynamic allocations for header path; serialization aims to minimize intermediate copies
- Envelope design avoids repeated type discovery; only `TypeName` string extracted first

---
## Extending To Other Transports

Transport abstraction lives behind `ISerialBus`.

### TCP (Implemented)

Use `SerialBusFactory.CreateTcp()` and the same `ProtocolConfiguration` (ports now mean: `LocalPort` for listener; `RemoteEndPoint` / `BroadcastEndPoints` for outgoing connections). Broadcast fan-out writes the framed message to each active TCP stream (inbound accepted + established outbound). Ordering is preserved per-connection (TCP guarantee) but different peers may drift due to scheduling.

Example:

```csharp
var tcpBus = SerialBusFactory.CreateTcp();
await tcpBus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 9000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9001),
    BroadcastEndPoints = new [] { new IPEndPoint(IPAddress.Loopback, 9002) },
    SerializationProtocol = SerializationProtocol.JsonRaw
});
tcpBus.SubscribeTo<Chat>(c => Console.WriteLine($"[TCP] {c.User}: {c.Text}"));
await tcpBus.SendAsync(new Chat("alice", "over tcp"));
```

### FTP (Planned)

`SerialBusFactory.CreateFtp()` is a stub today (throws). The intended design is a control channel for small messages and opportunistic file uploads for large payloads.

```csharp
try { var ftp = SerialBusFactory.CreateFtp(); }
catch (NotImplementedException) { /* expected for now */ }
```

Planned outline:

1. Lightweight command channel (possibly TCP) implementing `ISerialBus` semantics for control messages.
2. Escalation to file transfer for large payloads (chunked + resume).
3. Optional integrity (hash) verification & parallel segment streaming.

---

## Samples

See `sandbox/ClientA` and `sandbox/ClientB` for dual-process demonstration using both protocols simultaneously (Delimited + JSON) over loopback with independent ports.

Integration tests (`MultiBroadcastTests`, `TcpIntegrationTests`) show:

- UDP multi-endpoint broadcast
- TCP fan-out & ordering guarantees
- Concurrent full-mesh delivery
- Duplicate endpoint de-duplication

---

## Roadmap

- Additional transports: TCP first, then optional secure channel wrapper
- Binary packed serializer (struct layout aware)
- Source generator for zero-reflection fast path
- Optional compression & encryption layers
- Health / metrics callbacks

---

## FAQ

Q: Do both peers need the exact same CLR types?  
A: They need matching protocol type names and compatible property ordering (Delimited) or matching JSON contracts (JsonRaw). CLR assembly identity is not required.

Q: Can I mix serializers on the same socket?  
A: One `ISerialBus` instance uses one `SerializationProtocol`. Create multiple instances for mixed protocols.

Q: Is ordering enforced?  
A: Receiver trusts the order defined by `[Udp(n)]`. Reordering is a breaking change.

---

## Minimal Cheat Sheet

```csharp
var bus = SerialBusFactory.CreateUdp();
await bus.StartListeningAsync(new ProtocolConfiguration {
    LocalPort = 7000,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 7001),
    SerializationProtocol = SerializationProtocol.None
});

[UdpMessage("Chat")]
public record Chat([property: Udp(1)] string User, [property: Udp(2)] string Text);

bus.SubscribeTo<Chat>(c => Console.WriteLine($"{c.User}: {c.Text}"));
await bus.SendAsync(new Chat("me", "hi there"));
```

---

## License

MIT (add LICENSE file if not already present).

---

## Contributing

Issues & PRs welcome: add tests / samples for new serializers or transports.

---
Happy messaging!
