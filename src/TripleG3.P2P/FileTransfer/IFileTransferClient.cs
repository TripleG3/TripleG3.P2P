using System.Net;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.FileTransfer;

/// <summary>
/// Direct peer-to-peer file transfer endpoint. A client can initiate transfers and accept inbound requests.
/// </summary>
public interface IFileTransferClient : IAsyncDisposable
{
    /// <summary>Starts the local listener. Calling this more than once is safe.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the listener and cancels active transfers.</summary>
    ValueTask StopAsync();

    /// <summary>Requests a transfer from this peer to one or more configured endpoints.</summary>
    Task<IReadOnlyList<FileTransferResult>> SendAsync(
        string sourcePath,
        IReadOnlyCollection<IPEndPoint> peers,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Raised when a remote peer requests permission to send a file.</summary>
    event Func<FileTransferRequest, CancellationToken, ValueTask<FileTransferDecision>>? TransferRequested;

    /// <summary>Raised for listener, authorization, and individual transfer lifecycle changes.</summary>
    event EventHandler<P2PDiagnosticEventArgs>? Diagnostic;
}
