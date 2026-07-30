using System.Net;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.FileTransfer;

public sealed class FileTransferOptions
{
    public required IPEndPoint LocalEndPoint { get; init; }

    public long MaximumFileBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    public int BufferSize { get; init; } = 64 * 1024;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumConcurrentTransfers { get; init; } = 4;

    /// <summary>Optional host-owned authorization policy evaluated before any inbound file is accepted.</summary>
    public IPeerAuthorizer? PeerAuthorizer { get; init; }

    /// <summary>Optional approved session identifier supplied to the authorization policy.</summary>
    public string? SessionId { get; init; }

    /// <summary>Optional expected sender device identifier supplied to the authorization policy.</summary>
    public string? SenderDeviceId { get; init; }
}
