namespace TripleG3.P2P.Maui.Core;

/// <summary>
/// Abstraction for a bidirectional serial messaging bus. Implementations (UDP, TCP, etc.)
/// expose a unified API for starting a listener, subscribing to strongly-typed messages
/// and sending messages to a remote peer.
/// </summary>
public interface ISerialBus
{
    /// <summary>
    /// Indicates whether the bus has an active listening socket.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// Binds the local endpoint (port) and starts an asynchronous receive loop according
    /// to the supplied <see cref="ProtocolConfiguration"/>. Safe to call multiple times;
    /// subsequent calls while already listening are ignored.
    /// </summary>
    /// <param name="config">Protocol & transport configuration (remote endpoint, local port, serializer).</param>
    /// <param name="cancellationToken">Optional token to cancel startup.</param>
    ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the underlying connection / socket and cancels the receive loop.
    /// </summary>
    ValueTask CloseConnectionAsync();

    /// <summary>
    /// Subscribes a handler for messages whose protocol type name maps to <typeparamref name="T"/>.
    /// Multiple handlers per type are allowed; handlers are invoked on the receive loop thread.
    /// </summary>
    /// <typeparam name="T">Message contract type.</typeparam>
    /// <param name="handler">Delegate invoked with the deserialized message instance.</param>
    void SubscribeTo<T>(Action<T> handler);

    /// <summary>
    /// Sends a message of type <typeparamref name="T"/> to the configured remote endpoint using
    /// the bus instance's <see cref="ProtocolConfiguration.SerializationProtocol"/>.
    /// </summary>
    /// <typeparam name="T">Message contract type.</typeparam>
    /// <param name="message">Instance to serialize and send.</param>
    /// <param name="messageType">Logical message classification (currently Data).</param>
    /// <param name="cancellationToken">Optional cancellation token for the send operation.</param>
    ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default);
}
