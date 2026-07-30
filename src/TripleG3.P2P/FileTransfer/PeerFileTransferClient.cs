using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.FileTransfer;

/// <summary>
/// Direct TCP peer-to-peer file transfer client. Incoming transfers are opt-in through <see cref="IFileTransferClient.TransferRequested"/>.
/// </summary>
public sealed class PeerFileTransferClient : IFileTransferClient
{
    private readonly FileTransferOptions _options;
    private readonly SemaphoreSlim _transferGate;
    private readonly object _lifecycleGate = new();
    private readonly List<Task> _activeTasks = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _disposed;

    public PeerFileTransferClient(FileTransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.BufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumConcurrentTransfers <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _transferGate = new SemaphoreSlim(options.MaximumConcurrentTransfers);
    }

    public event Func<FileTransferRequest, CancellationToken, ValueTask<FileTransferDecision>>? TransferRequested;

    public event EventHandler<P2PDiagnosticEventArgs>? Diagnostic;

    public bool IsListening => Volatile.Read(ref _listener) is not null;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            if (_listener is not null) return ValueTask.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(_options.LocalEndPoint);
            _listener.Start();
            _acceptTask = AcceptLoopAsync(_listener, _cts.Token);
            Report(P2PDiagnosticKind.ListenerStarted, _options.LocalEndPoint);
        }
        return ValueTask.CompletedTask;
    }

    public async Task<IReadOnlyList<FileTransferResult>> SendAsync(
        string sourcePath,
        IReadOnlyCollection<IPEndPoint> peers,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(peers);
        if (peers.Count == 0) throw new ArgumentException("At least one peer is required.", nameof(peers));
        var metadata = await ReadMetadataAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var tasks = peers.Distinct().Select(peer => SendToPeerAsync(sourcePath, metadata, peer, progress, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async ValueTask StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        lock (_lifecycleGate)
        {
            listener = _listener;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
        }
        if (listener is null) return;
        cts?.Cancel();
        listener.Stop();
        Report(P2PDiagnosticKind.ListenerStopped, _options.LocalEndPoint);
        if (acceptTask is not null)
        {
            try { await acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Task[] active;
        lock (_activeTasks) active = [.. _activeTasks];
        if (active.Length > 0) await Task.WhenAll(active).ConfigureAwait(false);
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _transferGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
                if (!await IsAuthorizedAsync(peer, cancellationToken).ConfigureAwait(false))
                {
                    Report(P2PDiagnosticKind.AuthorizationRejected, peer, "Inbound file-transfer peer was rejected.");
                    client.Dispose();
                    continue;
                }

                var task = HandleIncomingAsync(client, cancellationToken);
                lock (_activeTasks) _activeTasks.Add(task);
                _ = task.ContinueWith(completed => { lock (_activeTasks) _activeTasks.Remove(completed); }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream? activeStream = null;
        using (client)
        {
            try
            {
                await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var stream = client.GetStream();
                    activeStream = stream;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.RequestTimeout);
                    var request = await FileTransferProtocol.ReadHeaderAsync(stream, timeout.Token).ConfigureAwait(false);
                    if (request.Kind is not (FileTransferProtocol.MessageKind.PushOffer or FileTransferProtocol.MessageKind.PullRequest)) throw new InvalidDataException("Unsupported transfer request.");
                    ValidateLength(request.Length);
                    var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
                    var decision = await DecideAsync(new FileTransferRequest(request.Id, peer, request.FileName, request.Length, request.Sha256), timeout.Token).ConfigureAwait(false);
                    if (!decision.Accepted)
                    {
                        await FileTransferProtocol.WriteDecisionAsync(stream, false, decision.Reason, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    if (request.Kind == FileTransferProtocol.MessageKind.PushOffer)
                    {
                        await FileTransferProtocol.WriteDecisionAsync(stream, true, null, timeout.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(decision.DestinationPath)) throw new InvalidDataException("Accepted transfer did not specify a destination path.");
                        var destination = ValidateDestinationPath(decision.DestinationPath, request.FileName);
                        try
                        {
                            Report(P2PDiagnosticKind.FileTransferStarted, peer, transferId: request.Id);
                            var result = await ReceiveBytesAsync(stream, request.Id, request.FileName, request.Length, request.Sha256, destination, peer, null, timeout.Token).ConfigureAwait(false);
                            await FileTransferProtocol.WriteDecisionAsync(stream, result.Succeeded, result.FailureReason, timeout.Token).ConfigureAwait(false);
                            Report(result.Succeeded ? P2PDiagnosticKind.FileTransferCompleted : P2PDiagnosticKind.FileTransferFailed, peer, result.FailureReason, request.Id);
                        }
                        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                        {
                            await FileTransferProtocol.WriteDecisionAsync(stream, false, "Receiver cancelled the transfer.", CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                finally { _transferGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (activeStream is not null)
                {
                    try
                    {
                        await FileTransferProtocol.WriteDecisionAsync(activeStream, false, exception.Message, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                }
            }
        }
    }

    private async Task<FileTransferResult> SendToPeerAsync(string sourcePath, FileMetadata metadata, IPEndPoint peer, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        await _transferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        try
        {
            Report(P2PDiagnosticKind.FileTransferStarted, peer, transferId: id);
            using var client = new TcpClient(peer.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            await client.ConnectAsync(peer.Address, peer.Port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await FileTransferProtocol.WriteHeaderAsync(stream, FileTransferProtocol.MessageKind.PushOffer, id, metadata.FileName, metadata.Length, metadata.Sha256, timeout.Token).ConfigureAwait(false);
            var decision = await FileTransferProtocol.ReadDecisionAsync(stream, timeout.Token).ConfigureAwait(false);
            if (!decision.Accepted) return Failed(id, peer, metadata, decision.Reason);
            await SendBytesAsync(stream, id, metadata, sourcePath, peer, progress, timeout.Token).ConfigureAwait(false);
            var completion = await FileTransferProtocol.ReadDecisionAsync(stream, timeout.Token).ConfigureAwait(false);
            var result = completion.Accepted ? new FileTransferResult(id, peer, metadata.FileName, metadata.Length, metadata.Sha256, true) : Failed(id, peer, metadata, completion.Reason);
            Report(result.Succeeded ? P2PDiagnosticKind.FileTransferCompleted : P2PDiagnosticKind.FileTransferFailed, peer, result.FailureReason, id);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var result = Failed(id, peer, metadata, exception.Message);
            Report(P2PDiagnosticKind.FileTransferFailed, peer, result.FailureReason, id);
            return result;
        }
        finally { _transferGate.Release(); }
    }

    private async Task<FileTransferResult> ReceiveBytesAsync(Stream stream, Guid id, string fileName, long length, string expectedHash, string destination, IPEndPoint peer, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + ".part";
        try
        {
            string actual;
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await CopyExactAsync(stream, output, hash, id, fileName, length, progress, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                return Failed(id, peer, new FileMetadata(fileName, length, actual), "SHA-256 integrity check failed.");
            }
            File.Move(temporary, destination, true);
            return new FileTransferResult(id, peer, fileName, length, actual, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private async Task SendBytesAsync(Stream stream, Guid id, FileMetadata metadata, string sourcePath, IPEndPoint peer, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, true);
        var buffer = new byte[_options.BufferSize];
        long sent = 0;
        while (sent < metadata.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, metadata.Length - sent)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Source file changed during transfer.");
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;
            progress?.Report(new FileTransferProgress(id, metadata.FileName, sent, metadata.Length));
        }
    }

    private async ValueTask<FileTransferDecision> DecideAsync(FileTransferRequest request, CancellationToken cancellationToken)
    {
        var handlers = TransferRequested;
        if (handlers is null) return FileTransferDecision.Reject("Receiver has not enabled inbound transfers.");
        FileTransferDecision? decision = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<FileTransferRequest, CancellationToken, ValueTask<FileTransferDecision>>>() )
        {
            decision = await handler(request, cancellationToken).ConfigureAwait(false);
            if (!decision.Accepted) return decision;
        }
        return decision ?? FileTransferDecision.Reject("Transfer was not accepted.");
    }

    private async Task<FileMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Source file was not found.", path);
        ValidateLength(info.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, true);
        var buffer = new byte[_options.BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        return new FileMetadata(info.Name, info.Length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private void ValidateLength(long length)
    {
        if (length < 0 || length > _options.MaximumFileBytes) throw new InvalidDataException("File exceeds the configured transfer limit.");
    }

    private async Task CopyExactAsync(Stream input, Stream output, IncrementalHash hash, Guid id, string name, long length, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.BufferSize];
        long copied = 0;
        while (copied < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(new FileTransferProgress(id, name, copied, length));
            Report(P2PDiagnosticKind.FileTransferProgress, transferId: id);
        }
    }

    private static FileTransferResult Failed(Guid id, IPEndPoint peer, FileMetadata metadata, string? reason) => new(id, peer, metadata.FileName, metadata.Length, metadata.Sha256, false, reason);

    private async ValueTask<bool> IsAuthorizedAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        if (_options.PeerAuthorizer is null) return true;
        return await _options.PeerAuthorizer.AuthorizeAsync(
            new PeerAuthorizationContext(_options.SessionId, _options.SenderDeviceId, peer, P2PResourceKind.FileTransfer),
            cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateDestinationPath(string destinationPath, string requestedFileName)
    {
        var destination = Path.GetFullPath(destinationPath);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(destination))) throw new InvalidDataException("Destination file name is invalid.");
        return destination;
    }

    private void Report(P2PDiagnosticKind kind, IPEndPoint? peer = null, string? message = null, Guid? transferId = null)
        => Diagnostic?.Invoke(this, new P2PDiagnosticEventArgs(kind, peer, _options.SessionId, message, transferId));

    private sealed record FileMetadata(string FileName, long Length, string Sha256);
}
