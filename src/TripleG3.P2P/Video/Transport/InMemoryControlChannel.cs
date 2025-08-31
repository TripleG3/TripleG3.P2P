using System.Collections.Concurrent;

namespace TripleG3.P2P.Video;

/// <summary>Simple in-memory loopback control channel for tests / local process pairing.</summary>
public sealed class InMemoryControlChannel : IControlChannel
{
    private readonly BlockingCollection<string> _queue = new();
    public event Action<string>? MessageReceived;

    public InMemoryControlChannel()
    {
        Task.Run(async () => {
            foreach (var msg in _queue.GetConsumingEnumerable())
            {
                MessageReceived?.Invoke(msg);
                await Task.Yield();
            }
        });
    }

    public Task SendReliableAsync(string message, CancellationToken ct = default)
    {
        _queue.Add(message, ct);
        return Task.CompletedTask;
    }
}
