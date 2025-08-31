namespace TripleG3.P2P.Video;

public interface IControlChannel
{
    event Action<string> MessageReceived;
    Task SendReliableAsync(string message, CancellationToken ct = default);
}
