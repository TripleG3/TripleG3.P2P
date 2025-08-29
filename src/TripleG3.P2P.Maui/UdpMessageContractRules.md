# Udp Buffer Contract Details

Create a service that can send / receive messages over a network via UDP.
The IP address that we will receive messages to/from will later come from an API so we need to be able to pass the IP address dynamically.
We do not need a fallback or error correction, this will be fire and forget. We will have a method to close the connection but that will also be accomplished manually.

Note: This is going to be a NuGet package to download for use in any MAUI .NET 9 project.
It must also have a convenient way to let us know what the message contract will look like and the ability to automatically serialize / deserialize the buffer to and from the contract.

## Architecture

- .NET 9.0 MAUI Application
- C# Programming Language
- Starting with UDP protocol but will later be updated to work with TCP, FTP, etc.
- Follow SOLID principles and expose interfaces and implementations for injection.
- Keep public interfaces between protocol's the same so that we don't care about the form of communication protocol but can communicate via the same interface. i.e. The same interface will work whether we choose a UDP implementation, TCP implementation, etc.
- Make messages immutable, preferably using records.
- Use `Span` and `Span<T>` whenever possible to make sending and receiving the message as fast as possible.

## Rules

- Sends / Received all messages as UDP.
- Converts type and information to UDP buffer unless otherwise told to keep the data raw (MessageType.None).
- All buffers will start with a UdpHeader (which will consume the first 8 values of the byte[] or buffer).
- The message will begin at the 9th position of the byte[] or buffer.
- The delimiter `@-@` used in the buffer will come after the first value in the buffer.
- You are allowed to break the rules if you understand the assignment and have a better implementation.

```csharp
byte[] = new[] { /* First 8 bytes represent the UdpHeader */};
```

## UdpHeader

- bytes 0-3 (Length of the buffer - the 8 bytes in the header) so we know how many bytes to consume for one item.
- bytes 4-5 (MessageType as a number)
- bytes 6-7 (Type of message so we know how to deserialize it)

## UdpAttribute

- Class and Property Level
- Class level doesn't require an int passed with it.
- Property level is telling us the order within the array.
- Read the array as (past the first 8 bytes for the header) continue to read until hit the 3 bytes '@-@'. Excluding the '@-@' bytes, the others would be converted to the type required by the UDP message.
- Example for `Person`
- - bytes (9-n) will be the `Envelope` with the `Person` object passed as the `Message` property within the `Envelope`.
- - The buffer can be read from `@-@` to `@-@` where every byte in between those will be deserialized the associated property. The value with the `UdpAttribute`, `(1)` for example, is how we know the order of the data in the buffer.
- - Example: For our `Person` `[Udp(1)]` will be deserialized as the `Name`. `[Udp(2)]` would be the `Age` and so on.

## Examples

- We should be able to communicate simple like this.
- We will add other protocols later like TCP but for now we're just using UDP. If we do add TCP then we want the logic broken out so that it can be easily tested and injected.

```csharp
public interface ISerialBus
{
    bool IsListening { get; }
    ValueTask StartListeningAsync(ProtocolConfiguration config);
    ValueTask CloseConnectionAsync();
    void SubscribeTo<T>(Action<T> message);
    ValueTask SendAsync<T>(T message);
}
```

Example:

```csharp
internal record UdpHeader(int Length, short MessageType, SerializationProtocol SerializationProtocol)
{
    internal static UdpHeader Empty { get; } = new(0, 0, SerializationProtocol.None);
}

public record Envelope<T>([property: Udp(1)] string TypeName, T? Message)
{
    public static Envelope<T> Empty { get; } = new(string.Empty, default);
}

public enum SerializationProtocol : short
{
    None, // Default behavior
    JsonRaw // Converts the types to JSON and sends the buffer json
}

[Udp]
public record Person([property: Udp(1)] string Name, [property: Udp(2)] int Age, [property: Udp(3)] Address Address)
{
    public static Person Empty { get; } = new(string.Empty, 0, Address.Empty);
}

[Udp]
public class Address([property: Udp(1)] string Street, [property: Udp(2)] string City, [property: Udp(3)] string State, [property: Udp(4)] string Zip)
{
    public static Address Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
```

Messages will be sent in an `Envelope` so that we know what type of message is being sent and can deserialize it properly.