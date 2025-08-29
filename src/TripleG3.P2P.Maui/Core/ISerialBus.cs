namespace TripleG3.P2P.Maui.Core;

public interface ISerialBus
{
    bool IsListening { get; }
    ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default);
    ValueTask CloseConnectionAsync();
    void SubscribeTo<T>(Action<T> handler);
    ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default);
}
