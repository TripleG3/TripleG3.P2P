using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripleG3.P2P.Video;

/// <summary>Owned in-memory reliable control channel for tests and local process pairing.</summary>
public sealed class InMemoryControlChannel : IControlChannel, IDisposable, IAsyncDisposable
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<InMemoryControlChannel> _logger;
    private readonly Task _pumpTask;
    private int _disposed;

    public InMemoryControlChannel(ILogger<InMemoryControlChannel>? logger = null)
    {
        _logger = logger ?? NullLogger<InMemoryControlChannel>.Instance;
        _pumpTask = PumpAsync(_cts.Token);
    }

    public event Action<string>? MessageReceived;

    public Task SendReliableAsync(string message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(message);
        return _channel.Writer.WriteAsync(message, ct).AsTask();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "An in-memory control-channel subscriber failed.");
            }
        }
    }
}