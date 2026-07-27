using System.Net;

namespace TripleG3.P2P.FileTransfer;

public sealed class FileTransferOptions
{
    public required IPEndPoint LocalEndPoint { get; init; }

    public long MaximumFileBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    public int BufferSize { get; init; } = 64 * 1024;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumConcurrentTransfers { get; init; } = 4;
}
