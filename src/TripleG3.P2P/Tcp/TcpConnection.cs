using System.Net.Sockets;

namespace TripleG3.P2P.Tcp;

internal sealed class TcpConnection : IDisposable
{
    private int _disposed;

    public TcpConnection(string key, TcpClient client)
    {
        Key = key;
        Client = client;
        Stream = client.GetStream();
    }

    public string Key { get; }

    public TcpClient Client { get; }

    public NetworkStream Stream { get; }

    public SemaphoreSlim SendGate { get; } = new(1, 1);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            await Stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Dispose();
    }
}