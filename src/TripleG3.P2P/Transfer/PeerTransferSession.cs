using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TripleG3.P2P.Transfer;

internal sealed class PeerTransferSession : IPeerTransferSession
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly PeerTransferSessionOptions options;
    private readonly PeerTransferFrameProtector frameProtector;
    private readonly bool initiator;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly Channel<QueuedFrame> controlFrames = Channel.CreateUnbounded<QueuedFrame>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<QueuedFrame> dataFrames;
    private readonly SemaphoreSlim writeSignal = new(0);
    private readonly ConcurrentDictionary<Guid, PeerTransferOperation> transfers = [];
    private readonly TaskCompletionSource<PeerTransferHelloAcknowledgement> handshakeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> sessionCloseAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object stateGate = new();
    private PeerTransferSessionSnapshot snapshot;
    private long lastRemoteActivityMilliseconds;
    private Task readTask = Task.CompletedTask;
    private Task writeTask = Task.CompletedTask;
    private Task heartbeatTask = Task.CompletedTask;
    private int started;
    private int terminated;
    private int disposed;

    private PeerTransferSession(TcpClient client, PeerTransferSessionOptions options, bool initiator)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.client = client;
        this.options = options;
        frameProtector = new PeerTransferFrameProtector(options);
        this.initiator = initiator;
        client.NoDelay = true;
        stream = client.GetStream();
        dataFrames = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(options.MaximumQueuedDataFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        snapshot = new PeerTransferSessionSnapshot(
            options.SessionId,
            options.LocalDeviceId,
            options.RemoteDeviceId,
            PeerTransferSessionState.Connecting,
            string.Empty,
            DateTimeOffset.UtcNow,
            1);
        lastRemoteActivityMilliseconds = Environment.TickCount64;
    }

    public PeerTransferSessionSnapshot Snapshot
    {
        get
        {
            lock (stateGate)
            {
                return snapshot;
            }
        }
    }

    public IReadOnlyList<PeerTransferSnapshot> Transfers =>
        [.. transfers.Values
            .Select(transfer => transfer.Snapshot)
            .OrderByDescending(transfer => transfer.StartedAt)
            .ThenBy(transfer => transfer.TransferId)];

    public event EventHandler<PeerTransferSessionStateChangedEventArgs>? StateChanged;

    public event EventHandler<PeerTransferStateChangedEventArgs>? TransferChanged;

    public event Func<PeerTransferOpenRequest, CancellationToken, ValueTask<PeerTransferDecision>>? TransferRequested;

    public event Action<IPeerTransfer>? InboundTransferOpened;

    internal static async Task<IPeerTransferSession> CreateAsync(
        TcpClient client,
        PeerTransferSessionOptions options,
        bool initiator,
        CancellationToken cancellationToken)
    {
        PeerTransferSession session = new(client, options, initiator);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.FailSession("The peer transfer session could not be established.");
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IPeerTransfer> OpenTransferAsync(
        PeerTransferDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (!descriptor.IsValid)
        {
            throw new ArgumentException("A valid peer transfer descriptor is required.", nameof(descriptor));
        }

        if (transfers.Count >= options.MaximumConcurrentTransfers)
        {
            throw new InvalidOperationException("The peer transfer session has reached its concurrent transfer limit.");
        }

        PeerTransferOperation operation = new(this, descriptor, isSender: true, options.MaximumBufferedDataFramesPerTransfer);
        if (!transfers.TryAdd(descriptor.TransferId, operation))
        {
            throw new InvalidOperationException("A transfer with the supplied identifier already exists in this session.");
        }

        try
        {
            byte[] payload = PeerTransferWireProtocol.SerializeControl(
                new PeerTransferOpenPayload(descriptor),
                options.MaximumControlPayloadBytes);
            await SendControlAsync(PeerTransferFrameKind.TransferOpen, descriptor.TransferId, payload, cancellationToken).ConfigureAwait(false);
            PeerTransferOpenAcknowledgement acknowledgement = await operation
                .WaitForOpenAcknowledgementAsync(options.ControlTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!acknowledgement.Accepted)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(acknowledgement.Reason)
                    ? "The remote peer declined the transfer."
                    : acknowledgement.Reason);
            }

            operation.MoveToStreaming();
            return operation;
        }
        catch (Exception exception)
        {
            operation.Fail(exception.Message);
            throw;
        }
    }

    public async Task<PeerTransferSnapshot> QueryTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("A transfer identifier is required.", nameof(transferId));
        }

        if (!transfers.TryGetValue(transferId, out PeerTransferOperation? operation))
        {
            throw new KeyNotFoundException("The requested transfer is not known by this session.");
        }

        return await operation.QueryRemoteAsync(options.ControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelTransferAsync(Guid transferId, string reason, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (transferId == Guid.Empty)
        {
            throw new ArgumentException("A transfer identifier is required.", nameof(transferId));
        }

        if (!transfers.TryGetValue(transferId, out PeerTransferOperation? operation))
        {
            throw new KeyNotFoundException("The requested transfer is not known by this session.");
        }

        LocalCancellation cancellation = operation.BeginLocalCancellation(reason);
        if (cancellation.SendCancellation)
        {
            byte[] payload = PeerTransferWireProtocol.SerializeControl(
                new PeerTransferCancellationPayload(cancellation.RequestId, reason ?? string.Empty),
                options.MaximumControlPayloadBytes);
            await SendControlAsync(PeerTransferFrameKind.Cancel, transferId, payload, cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource cancellationTimeout = CreateControlTimeout(cancellationToken);
        try
        {
            await cancellation.Completion
            .WaitAsync(cancellationTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            operation.Fail("Timed out waiting for the remote peer to acknowledge transfer cancellation.");
            throw new TimeoutException("Timed out waiting for the remote peer to acknowledge transfer cancellation.");
        }
    }

    public bool TryGetTransfer(Guid transferId, out PeerTransferSnapshot transfer)
    {
        if (transfers.TryGetValue(transferId, out PeerTransferOperation? operation))
        {
            transfer = operation.Snapshot;
            return true;
        }

        transfer = PeerTransferSnapshot.Empty;
        return false;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        PeerTransferSessionState currentState = Snapshot.State;
        if (currentState is PeerTransferSessionState.Closed or PeerTransferSessionState.Failed)
        {
            return;
        }

        TransitionSession(PeerTransferSessionState.Closing, "Closing the peer transfer session.");
        try
        {
            await SendControlAsync(PeerTransferFrameKind.SessionClose, Guid.Empty, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            try
            {
                using CancellationTokenSource closeTimeout = CreateControlTimeout(cancellationToken);
                await sessionCloseAcknowledgement.Task
                    .WaitAsync(closeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            if (Snapshot.State != PeerTransferSessionState.Failed)
            {
                Terminate(PeerTransferSessionState.Closed, "The peer transfer session was closed.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            FailSession("The peer transfer session was disposed.");
        }

        await AwaitIgnoringCancellationAsync(readTask).ConfigureAwait(false);
        await AwaitIgnoringCancellationAsync(writeTask).ConfigureAwait(false);
        await AwaitIgnoringCancellationAsync(heartbeatTask).ConfigureAwait(false);
        frameProtector.Dispose();
        lifetimeCancellation.Dispose();
        writeSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The peer transfer session has already started.");
        }

        readTask = ReadLoopAsync(lifetimeCancellation.Token);
        writeTask = WriteLoopAsync(lifetimeCancellation.Token);
        heartbeatTask = HeartbeatLoopAsync(lifetimeCancellation.Token);
        if (initiator)
        {
            byte[] hello = PeerTransferWireProtocol.SerializeControl(
                new PeerTransferSessionHello(
                    options.SessionId,
                    options.LocalDeviceId,
                    options.RemoteDeviceId,
                    options.SessionGrant),
                options.MaximumControlPayloadBytes);
            await SendControlAsync(PeerTransferFrameKind.Hello, Guid.Empty, hello, cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource handshakeTimeout = CreateHandshakeTimeout(cancellationToken);
        try
        {
            await handshakeCompletion.Task
            .WaitAsync(handshakeTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            FailSession("Timed out waiting for the peer transfer session handshake.");
            throw new TimeoutException("Timed out waiting for the peer transfer session handshake.");
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PeerTransferFrame frame = await PeerTransferWireProtocol
                    .ReadAsync(stream, options.MaximumFrameBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (frame.SessionId != options.SessionId)
                {
                    throw new InvalidDataException("The peer transfer frame belongs to a different session.");
                }

                if (RequiresProtection(frame.Kind))
                {
                    frame = frame with { Payload = frameProtector.Unprotect(frame) };
                }

                Interlocked.Exchange(ref lastRemoteActivityMilliseconds, Environment.TickCount64);
                await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailSession($"The peer transfer connection failed: {exception.Message}");
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await writeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (controlFrames.Reader.TryRead(out QueuedFrame? control))
                {
                    await WriteFrameAsync(control!, cancellationToken).ConfigureAwait(false);
                }

                if (dataFrames.Reader.TryRead(out QueuedFrame? data))
                {
                    await WriteFrameAsync(data!, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailSession($"The peer transfer connection could not write a frame: {exception.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Snapshot.State != PeerTransferSessionState.Connected)
                {
                    continue;
                }

                long idleMilliseconds = Environment.TickCount64 - Interlocked.Read(ref lastRemoteActivityMilliseconds);
                if (idleMilliseconds > (long)options.SessionInactivityTimeout.TotalMilliseconds)
                {
                    FailSession("The peer transfer session became inactive.");
                    return;
                }

                await SendControlAsync(PeerTransferFrameKind.Ping, Guid.Empty, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailSession($"The peer transfer heartbeat failed: {exception.Message}");
        }
    }

    private async Task HandleFrameAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case PeerTransferFrameKind.Hello:
                await HandleHelloAsync(frame, cancellationToken).ConfigureAwait(false);
                return;
            case PeerTransferFrameKind.HelloAcknowledgement:
                HandleHelloAcknowledgement(frame);
                return;
            case PeerTransferFrameKind.SessionClose:
                await HandleSessionCloseAsync(cancellationToken).ConfigureAwait(false);
                return;
            case PeerTransferFrameKind.SessionCloseAcknowledgement:
                sessionCloseAcknowledgement.TrySetResult(true);
                return;
        }

        EnsureConnected();
        switch (frame.Kind)
        {
            case PeerTransferFrameKind.TransferOpen:
                await HandleTransferOpenAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case PeerTransferFrameKind.TransferOpenAcknowledgement:
                HandleTransferOpenAcknowledgement(frame);
                break;
            case PeerTransferFrameKind.Data:
                await HandleDataAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case PeerTransferFrameKind.SenderCompleted:
                HandleSenderCompleted(frame);
                break;
            case PeerTransferFrameKind.TransferCompleted:
                HandleTransferCompleted(frame);
                break;
            case PeerTransferFrameKind.TransferFailed:
                HandleTransferFailed(frame);
                break;
            case PeerTransferFrameKind.Cancel:
                await HandleCancelAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case PeerTransferFrameKind.CancelAcknowledgement:
                HandleCancelAcknowledgement(frame);
                break;
            case PeerTransferFrameKind.Query:
                await HandleQueryAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case PeerTransferFrameKind.Status:
                HandleStatus(frame);
                break;
            case PeerTransferFrameKind.Ping:
                await SendControlAsync(PeerTransferFrameKind.Pong, Guid.Empty, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                break;
            case PeerTransferFrameKind.Pong:
                break;
            default:
                throw new InvalidDataException("Unsupported peer transfer frame.");
        }
    }

    private async Task HandleHelloAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        if (initiator || Snapshot.State != PeerTransferSessionState.Connecting || frame.TransferId != Guid.Empty)
        {
            throw new InvalidDataException("Unexpected peer transfer session hello frame.");
        }

        PeerTransferSessionHello hello = PeerTransferWireProtocol.DeserializeControl<PeerTransferSessionHello>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        string validationFailure = ValidateHello(hello);
        PeerTransferSessionAuthorization authorization = string.IsNullOrEmpty(validationFailure)
            ? options.Authorizer is null
                ? PeerTransferSessionAuthorization.Allow()
                : await options.Authorizer.AuthorizeAsync(hello, cancellationToken).ConfigureAwait(false)
            : PeerTransferSessionAuthorization.Deny(validationFailure);
        PeerTransferHelloAcknowledgement acknowledgement = new(
            authorization.Accepted,
            authorization.Reason,
            options.LocalDeviceId,
            options.RemoteDeviceId);
        await SendControlAsync(
            PeerTransferFrameKind.HelloAcknowledgement,
            Guid.Empty,
            PeerTransferWireProtocol.SerializeControl(acknowledgement, options.MaximumControlPayloadBytes),
            cancellationToken).ConfigureAwait(false);
        if (!authorization.Accepted)
        {
            InvalidOperationException exception = new(string.IsNullOrWhiteSpace(authorization.Reason)
                ? "The peer transfer session was not authorized."
                : authorization.Reason);
            handshakeCompletion.TrySetException(exception);
            FailSession(exception.Message);
            return;
        }

        TransitionSession(PeerTransferSessionState.Connected, string.Empty);
        handshakeCompletion.TrySetResult(acknowledgement);
    }

    private void HandleHelloAcknowledgement(PeerTransferFrame frame)
    {
        if (!initiator || Snapshot.State != PeerTransferSessionState.Connecting || frame.TransferId != Guid.Empty)
        {
            throw new InvalidDataException("Unexpected peer transfer session hello acknowledgement.");
        }

        PeerTransferHelloAcknowledgement acknowledgement = PeerTransferWireProtocol.DeserializeControl<PeerTransferHelloAcknowledgement>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        if (!string.Equals(acknowledgement.SenderDeviceId, options.RemoteDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acknowledgement.ReceiverDeviceId, options.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The peer transfer session acknowledgement does not match the expected devices.");
        }

        if (!acknowledgement.Accepted)
        {
            InvalidOperationException exception = new(string.IsNullOrWhiteSpace(acknowledgement.Reason)
                ? "The peer transfer session was rejected."
                : acknowledgement.Reason);
            handshakeCompletion.TrySetException(exception);
            FailSession(exception.Message);
            return;
        }

        TransitionSession(PeerTransferSessionState.Connected, string.Empty);
        handshakeCompletion.TrySetResult(acknowledgement);
    }

    private async Task HandleTransferOpenAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        if (frame.TransferId == Guid.Empty)
        {
            throw new InvalidDataException("A peer transfer open frame requires a transfer identifier.");
        }

        PeerTransferOpenPayload payload = PeerTransferWireProtocol.DeserializeControl<PeerTransferOpenPayload>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        PeerTransferDescriptor descriptor = payload.Descriptor ?? PeerTransferDescriptor.Empty;
        if (descriptor.TransferId != frame.TransferId || !descriptor.IsValid)
        {
            await SendTransferOpenAcknowledgementAsync(frame.TransferId, PeerTransferDecision.Reject("The transfer descriptor is invalid."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? existing))
        {
            PeerTransferSnapshot existingSnapshot = existing.Snapshot;
            PeerTransferDecision duplicateDecision = existingSnapshot.IsTerminal
                ? PeerTransferDecision.Reject(existingSnapshot.TerminalReason)
                : PeerTransferDecision.Accept();
            await SendTransferOpenAcknowledgementAsync(frame.TransferId, duplicateDecision, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (transfers.Count >= options.MaximumConcurrentTransfers)
        {
            await SendTransferOpenAcknowledgementAsync(frame.TransferId, PeerTransferDecision.Reject("The peer transfer session has reached its concurrent transfer limit."), cancellationToken).ConfigureAwait(false);
            return;
        }

        PeerTransferOperation operation = new(this, descriptor, isSender: false, options.MaximumBufferedDataFramesPerTransfer);
        if (!transfers.TryAdd(frame.TransferId, operation))
        {
            await SendTransferOpenAcknowledgementAsync(frame.TransferId, PeerTransferDecision.Reject("A transfer with the supplied identifier already exists."), cancellationToken).ConfigureAwait(false);
            return;
        }

        PeerTransferDecision decision = await DecideInboundTransferAsync(operation, cancellationToken).ConfigureAwait(false);
        if (!decision.Accepted)
        {
            operation.Fail(decision.Reason);
            await SendTransferOpenAcknowledgementAsync(frame.TransferId, decision, cancellationToken).ConfigureAwait(false);
            return;
        }

        operation.MoveToStreaming();
        await SendTransferOpenAcknowledgementAsync(frame.TransferId, decision, cancellationToken).ConfigureAwait(false);
        NotifyInboundTransferOpened(operation);
    }

    private void HandleTransferOpenAcknowledgement(PeerTransferFrame frame)
    {
        if (!transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation) || !operation.IsSender)
        {
            return;
        }

        PeerTransferOpenAcknowledgement acknowledgement = PeerTransferWireProtocol.DeserializeControl<PeerTransferOpenAcknowledgement>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        operation.ApplyOpenAcknowledgement(acknowledgement);
    }

    private async Task HandleDataAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        if (!transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation) || operation.IsSender)
        {
            throw new InvalidDataException("The peer transfer data frame does not belong to an active inbound transfer.");
        }

        if (!operation.TryReceiveData(frame.Payload))
        {
            operation.Fail("The inbound payload handler did not consume data fast enough.");
            await SendControlAsync(
                PeerTransferFrameKind.TransferFailed,
                frame.TransferId,
                PeerTransferWireProtocol.SerializeControl(new PeerTransferFailurePayload("The inbound payload handler did not consume data fast enough."), options.MaximumControlPayloadBytes),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleSenderCompleted(PeerTransferFrame frame)
    {
        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation) && !operation.IsSender)
        {
            operation.MarkSenderCompleted();
        }
    }

    private void HandleTransferCompleted(PeerTransferFrame frame)
    {
        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation) && operation.IsSender)
        {
            operation.CompleteFromRemote();
        }
    }

    private void HandleTransferFailed(PeerTransferFrame frame)
    {
        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation))
        {
            PeerTransferFailurePayload failure = PeerTransferWireProtocol.DeserializeControl<PeerTransferFailurePayload>(
                frame.Payload,
                options.MaximumControlPayloadBytes);
            operation.Fail(failure.Reason);
        }
    }

    private async Task HandleCancelAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        PeerTransferCancellationPayload cancellation = PeerTransferWireProtocol.DeserializeControl<PeerTransferCancellationPayload>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        PeerTransferCancellationAcknowledgement acknowledgement;
        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation))
        {
            acknowledgement = operation.CancelFromRemote(cancellation.RequestId, cancellation.Reason);
        }
        else
        {
            acknowledgement = new PeerTransferCancellationAcknowledgement(
                cancellation.RequestId,
                PeerTransferState.Failed,
                "The transfer is not known by this session.");
        }

        await SendControlAsync(
            PeerTransferFrameKind.CancelAcknowledgement,
            frame.TransferId,
            PeerTransferWireProtocol.SerializeControl(acknowledgement, options.MaximumControlPayloadBytes),
            cancellationToken).ConfigureAwait(false);
    }

    private void HandleCancelAcknowledgement(PeerTransferFrame frame)
    {
        if (!transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation))
        {
            return;
        }

        PeerTransferCancellationAcknowledgement acknowledgement = PeerTransferWireProtocol.DeserializeControl<PeerTransferCancellationAcknowledgement>(
            frame.Payload,
            options.MaximumControlPayloadBytes);
        operation.ApplyCancellationAcknowledgement(acknowledgement);
    }

    private async Task HandleQueryAsync(PeerTransferFrame frame, CancellationToken cancellationToken)
    {
        PeerTransferSnapshot transfer = transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation)
            ? operation.Snapshot
            : PeerTransferSnapshot.Empty with
            {
                SessionId = options.SessionId,
                TransferId = frame.TransferId,
                State = PeerTransferState.Failed,
                TerminalReason = "The transfer is not known by this session.",
                CompletedAt = DateTimeOffset.UtcNow,
                Revision = 1
            };
        await SendControlAsync(
            PeerTransferFrameKind.Status,
            frame.TransferId,
            PeerTransferWireProtocol.SerializeControl(new PeerTransferStatusPayload(transfer), options.MaximumControlPayloadBytes),
            cancellationToken).ConfigureAwait(false);
    }

    private void HandleStatus(PeerTransferFrame frame)
    {
        if (transfers.TryGetValue(frame.TransferId, out PeerTransferOperation? operation))
        {
            PeerTransferStatusPayload payload = PeerTransferWireProtocol.DeserializeControl<PeerTransferStatusPayload>(
                frame.Payload,
                options.MaximumControlPayloadBytes);
            operation.ApplyRemoteStatus(payload.Snapshot);
        }
    }

    private async Task HandleSessionCloseAsync(CancellationToken cancellationToken)
    {
        await SendControlAsync(PeerTransferFrameKind.SessionCloseAcknowledgement, Guid.Empty, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        Terminate(PeerTransferSessionState.Closed, "The remote peer closed the transfer session.");
    }

    private async Task SendTransferOpenAcknowledgementAsync(
        Guid transferId,
        PeerTransferDecision decision,
        CancellationToken cancellationToken)
    {
        await SendControlAsync(
            PeerTransferFrameKind.TransferOpenAcknowledgement,
            transferId,
            PeerTransferWireProtocol.SerializeControl(
                new PeerTransferOpenAcknowledgement(decision.Accepted, decision.Reason),
                options.MaximumControlPayloadBytes),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PeerTransferDecision> DecideInboundTransferAsync(
        PeerTransferOperation operation,
        CancellationToken cancellationToken)
    {
        Func<PeerTransferOpenRequest, CancellationToken, ValueTask<PeerTransferDecision>>? handlers = TransferRequested;
        if (handlers is null)
        {
            return PeerTransferDecision.Reject("No payload handler accepted the inbound transfer.");
        }

        PeerTransferDecision decision = PeerTransferDecision.Reject("No payload handler accepted the inbound transfer.");
        PeerTransferOpenRequest request = new(operation.Descriptor, operation);
        foreach (Func<PeerTransferOpenRequest, CancellationToken, ValueTask<PeerTransferDecision>> handler in handlers.GetInvocationList().Cast<Func<PeerTransferOpenRequest, CancellationToken, ValueTask<PeerTransferDecision>>>())
        {
            decision = await handler(request, cancellationToken).ConfigureAwait(false);
            if (!decision.Accepted)
            {
                return decision;
            }
        }

        return decision;
    }

    private void NotifyInboundTransferOpened(PeerTransferOperation operation)
    {
        Action<IPeerTransfer>? handlers = InboundTransferOpened;
        if (handlers is null)
        {
            _ = FailInboundTransferWithoutHandlerAsync(operation);
            return;
        }

        foreach (Action<IPeerTransfer> handler in handlers.GetInvocationList().Cast<Action<IPeerTransfer>>())
        {
            try
            {
                handler(operation);
            }
            catch (Exception exception)
            {
                _ = FailInboundTransferAsync(operation, exception.Message);
            }
        }
    }

    private Task FailInboundTransferWithoutHandlerAsync(PeerTransferOperation operation) =>
        FailInboundTransferAsync(operation, "No payload handler started the inbound transfer.");

    private async Task FailInboundTransferAsync(PeerTransferOperation operation, string reason)
    {
        operation.Fail(reason);
        try
        {
            await SendControlAsync(
                PeerTransferFrameKind.TransferFailed,
                operation.Descriptor.TransferId,
                PeerTransferWireProtocol.SerializeControl(new PeerTransferFailurePayload(reason), options.MaximumControlPayloadBytes),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task SendControlAsync(
        PeerTransferFrameKind kind,
        Guid transferId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfTerminated();
        TaskCompletionSource<bool> written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueuedFrame frame = new(new PeerTransferFrame(kind, options.SessionId, transferId, payload), written);
        await EnqueueAsync(controlFrames.Writer, frame, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
        await written.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    internal async Task SendDataAsync(Guid transferId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfTerminated();
        if (data.Length > MaximumPlainDataFrameBytes)
        {
            throw new InvalidDataException("The payload chunk exceeds the session frame limit.");
        }

        if (data.IsEmpty)
        {
            return;
        }

        QueuedFrame frame = new(new PeerTransferFrame(PeerTransferFrameKind.Data, options.SessionId, transferId, data.ToArray()), null);
        await EnqueueAsync(dataFrames.Writer, frame, cancellationToken).ConfigureAwait(false);
    }

    internal Task SendSenderCompletedAsync(Guid transferId, CancellationToken cancellationToken) =>
        SendControlAsync(PeerTransferFrameKind.SenderCompleted, transferId, ReadOnlyMemory<byte>.Empty, cancellationToken);

    internal Task SendTransferCompletedAsync(Guid transferId, CancellationToken cancellationToken) =>
        SendControlAsync(PeerTransferFrameKind.TransferCompleted, transferId, ReadOnlyMemory<byte>.Empty, cancellationToken);

    internal Task CancelFromTransferAsync(Guid transferId, string reason, CancellationToken cancellationToken) =>
        CancelTransferAsync(transferId, reason, cancellationToken);

    internal async Task FailTransferAsync(Guid transferId, string reason, CancellationToken cancellationToken)
    {
        if (!transfers.TryGetValue(transferId, out PeerTransferOperation? operation))
        {
            throw new KeyNotFoundException("The requested transfer is not known by this session.");
        }

        PeerTransferSnapshot terminal = operation.Fail(reason);
        if (terminal.State != PeerTransferState.Failed)
        {
            return;
        }

        await SendControlAsync(
            PeerTransferFrameKind.TransferFailed,
            transferId,
            PeerTransferWireProtocol.SerializeControl(
                new PeerTransferFailurePayload(terminal.TerminalReason),
                options.MaximumControlPayloadBytes),
            cancellationToken).ConfigureAwait(false);
    }

    internal void ReportTransferChanged(PeerTransferSnapshot transfer)
    {
        EventHandler<PeerTransferStateChangedEventArgs>? handlers = TransferChanged;
        if (handlers is null)
        {
            return;
        }

        PeerTransferStateChangedEventArgs eventArgs = new(transfer);
        foreach (EventHandler<PeerTransferStateChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<PeerTransferStateChangedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
            }
        }
    }

    private async Task EnqueueAsync(
        ChannelWriter<QueuedFrame> writer,
        QueuedFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
            await writer.WriteAsync(frame, linked.Token).ConfigureAwait(false);
            writeSignal.Release();
        }
        catch (Exception exception)
        {
            frame.Written?.TrySetException(exception);
            throw;
        }
    }

    private async Task WriteFrameAsync(QueuedFrame queued, CancellationToken cancellationToken)
    {
        try
        {
            PeerTransferFrame frame = queued.Frame;
            if (RequiresProtection(frame.Kind))
            {
                frame = frame with { Payload = frameProtector.Protect(frame) };
            }

            await PeerTransferWireProtocol.WriteAsync(stream, frame, options.MaximumFrameBytes, cancellationToken).ConfigureAwait(false);
            queued.Written?.TrySetResult(true);
        }
        catch (Exception exception)
        {
            queued.Written?.TrySetException(exception);
            throw;
        }
    }

    private void TransitionSession(PeerTransferSessionState state, string message)
    {
        PeerTransferSessionSnapshot changed;
        lock (stateGate)
        {
            if (snapshot.State == state && string.Equals(snapshot.Message, message, StringComparison.Ordinal))
            {
                return;
            }

            changed = snapshot with
            {
                State = state,
                Message = message ?? string.Empty,
                ChangedAt = DateTimeOffset.UtcNow,
                Revision = snapshot.Revision + 1
            };
            snapshot = changed;
        }

        EventHandler<PeerTransferSessionStateChangedEventArgs>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        PeerTransferSessionStateChangedEventArgs eventArgs = new(changed);
        foreach (EventHandler<PeerTransferSessionStateChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<PeerTransferSessionStateChangedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
            }
        }
    }

    private void EnsureConnected()
    {
        if (Snapshot.State != PeerTransferSessionState.Connected)
        {
            throw new InvalidOperationException("The peer transfer session is not connected.");
        }
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref terminated) != 0)
        {
            throw new InvalidOperationException("The peer transfer session has ended.");
        }
    }

    private static bool RequiresProtection(PeerTransferFrameKind kind) =>
        kind is not PeerTransferFrameKind.Hello and not PeerTransferFrameKind.HelloAcknowledgement;

    private int MaximumPlainDataFrameBytes => options.MaximumFrameBytes - PeerTransferFrameProtector.AuthenticationOverheadBytes;

    private string ValidateHello(PeerTransferSessionHello hello)
    {
        if (hello.SessionId != options.SessionId)
        {
            return "The peer transfer session identifier is invalid.";
        }

        if (!string.Equals(hello.SenderDeviceId, options.RemoteDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return "The peer transfer sender device is invalid.";
        }

        if (!string.Equals(hello.ReceiverDeviceId, options.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return "The peer transfer receiving device is invalid.";
        }

        return string.Empty;
    }

    private CancellationTokenSource CreateHandshakeTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
        linked.CancelAfter(options.HandshakeTimeout);
        return linked;
    }

    private CancellationTokenSource CreateControlTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked = CreateLinkedToken(cancellationToken);
        linked.CancelAfter(options.ControlTimeout);
        return linked;
    }

    private CancellationTokenSource CreateLinkedToken(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);

    private void FailSession(string message)
    {
        if (Snapshot.State is PeerTransferSessionState.Closed or PeerTransferSessionState.Failed)
        {
            return;
        }

        Terminate(PeerTransferSessionState.Failed, message);
    }

    private void Terminate(PeerTransferSessionState terminalState, string message)
    {
        if (Interlocked.Exchange(ref terminated, 1) != 0)
        {
            return;
        }

        TransitionSession(terminalState, message);
        foreach (PeerTransferOperation operation in transfers.Values)
        {
            if (!operation.Snapshot.IsTerminal)
            {
                operation.Fail($"The peer transfer session ended: {message}");
            }
        }

        InvalidOperationException exception = new(message);
        handshakeCompletion.TrySetException(exception);
        sessionCloseAcknowledgement.TrySetResult(false);
        lifetimeCancellation.Cancel();
        controlFrames.Writer.TryComplete(exception);
        dataFrames.Writer.TryComplete(exception);
        FailQueuedFrames(controlFrames.Reader, exception);
        FailQueuedFrames(dataFrames.Reader, exception);
        client.Dispose();
    }

    private static void FailQueuedFrames(ChannelReader<QueuedFrame> reader, Exception exception)
    {
        while (reader.TryRead(out QueuedFrame? frame))
        {
            frame?.Written?.TrySetException(exception);
        }
    }

    private static async Task AwaitIgnoringCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private sealed record QueuedFrame(PeerTransferFrame Frame, TaskCompletionSource<bool>? Written);

    private sealed record LocalCancellation(Guid RequestId, bool SendCancellation, Task<PeerTransferSnapshot> Completion);

    private sealed class PeerTransferOperation : IPeerTransfer
    {
        private readonly PeerTransferSession owner;
        private readonly bool isSender;
        private readonly Channel<byte[]> inboundData;
        private readonly CancellationTokenSource transferCancellation = new();
        private readonly TaskCompletionSource<PeerTransferOpenAcknowledgement> openAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PeerTransferSnapshot> terminalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim queryGate = new(1, 1);
        private readonly object operationGate = new();
        private PeerTransferSnapshot snapshot;
        private Guid localCancellationRequestId;
        private bool senderCompletionSent;
        private bool remoteSenderCompleted;
        private TaskCompletionSource<PeerTransferSnapshot>? remoteStatusCompletion;

        public PeerTransferOperation(
            PeerTransferSession owner,
            PeerTransferDescriptor descriptor,
            bool isSender,
            int maximumBufferedDataFrames)
        {
            this.owner = owner;
            Descriptor = descriptor;
            this.isSender = isSender;
            inboundData = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maximumBufferedDataFrames)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
            snapshot = new PeerTransferSnapshot(
                owner.options.SessionId,
                descriptor.TransferId,
                descriptor.Kind,
                PeerTransferState.Opening,
                descriptor.DisplayName,
                descriptor.Summary,
                descriptor.ExpectedLength,
                0,
                descriptor.IntegrityHash,
                string.Empty,
                DateTimeOffset.UtcNow,
                default,
                1);
        }

        public PeerTransferDescriptor Descriptor { get; }

        public bool IsSender => isSender;

        public int MaximumDataFrameBytes => owner.MaximumPlainDataFrameBytes;

        public PeerTransferSnapshot Snapshot
        {
            get
            {
                lock (operationGate)
                {
                    return snapshot;
                }
            }
        }

        public CancellationToken TransferCancellationToken => transferCancellation.Token;

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (!isSender)
            {
                throw new InvalidOperationException("Only the sending peer can write payload data.");
            }

            EnsureStreaming();
            await owner.SendDataAsync(Descriptor.TransferId, data, cancellationToken).ConfigureAwait(false);
            AddTransferredBytes(data.Length);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (isSender)
            {
                throw new InvalidOperationException("Only the receiving peer can read payload data.");
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transferCancellation.Token);
            await foreach (byte[] data in inboundData.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                yield return data;
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (isSender)
            {
                bool sendCompletion = BeginSenderCompletion();
                if (sendCompletion)
                {
                    await owner.SendSenderCompletedAsync(Descriptor.TransferId, cancellationToken).ConfigureAwait(false);
                }

                PeerTransferSnapshot terminal = await terminalCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfNotCompleted(terminal);
                return;
            }

            if (!HasRemoteSenderCompleted())
            {
                throw new InvalidOperationException("The remote sender has not completed the inbound payload.");
            }

            PeerTransferSnapshot current = Snapshot;
            if (current.IsTerminal)
            {
                ThrowIfNotCompleted(current);
                return;
            }

            MoveToCompleting();
            await owner.SendTransferCompletedAsync(Descriptor.TransferId, cancellationToken).ConfigureAwait(false);
            ThrowIfNotCompleted(CompleteTerminal(PeerTransferState.Completed, string.Empty));
        }

        public Task CancelAsync(string reason, CancellationToken cancellationToken = default) =>
            owner.CancelFromTransferAsync(Descriptor.TransferId, reason, cancellationToken);

        public Task FailAsync(string reason, CancellationToken cancellationToken = default) =>
            owner.FailTransferAsync(Descriptor.TransferId, reason, cancellationToken);

        public void MoveToStreaming()
        {
            PeerTransferSnapshot? changed = null;
            lock (operationGate)
            {
                if (snapshot.IsTerminal || snapshot.State == PeerTransferState.Streaming)
                {
                    return;
                }

                snapshot = snapshot with
                {
                    State = PeerTransferState.Streaming,
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            owner.ReportTransferChanged(changed);
        }

        public async Task<PeerTransferOpenAcknowledgement> WaitForOpenAcknowledgementAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transferCancellation.Token);
            linked.CancelAfter(timeout);
            try
            {
                return await openAcknowledgement.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PeerTransferOpenAcknowledgement(false, "Timed out waiting for the remote peer to accept the transfer.");
            }
        }

        public void ApplyOpenAcknowledgement(PeerTransferOpenAcknowledgement acknowledgement)
        {
            openAcknowledgement.TrySetResult(acknowledgement);
            if (!acknowledgement.Accepted)
            {
                Fail(acknowledgement.Reason);
            }
        }

        public bool TryReceiveData(ReadOnlyMemory<byte> data)
        {
            lock (operationGate)
            {
                if (snapshot.IsTerminal)
                {
                    return true;
                }
            }

            if (!inboundData.Writer.TryWrite(data.ToArray()))
            {
                return false;
            }

            AddTransferredBytes(data.Length);
            return true;
        }

        public void MarkSenderCompleted()
        {
            PeerTransferSnapshot? changed = null;
            lock (operationGate)
            {
                if (snapshot.IsTerminal || remoteSenderCompleted)
                {
                    return;
                }

                remoteSenderCompleted = true;
                snapshot = snapshot with
                {
                    State = PeerTransferState.Completing,
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            inboundData.Writer.TryComplete();
            owner.ReportTransferChanged(changed);
        }

        public void CompleteFromRemote() => CompleteTerminal(PeerTransferState.Completed, string.Empty);

        public PeerTransferSnapshot Fail(string reason) => CompleteTerminal(
            PeerTransferState.Failed,
            string.IsNullOrWhiteSpace(reason) ? "The peer transfer failed." : reason);

        public PeerTransferCancellationAcknowledgement CancelFromRemote(Guid requestId, string reason)
        {
            PeerTransferSnapshot terminal = Snapshot.IsTerminal
                ? Snapshot
                : CompleteTerminal(PeerTransferState.Cancelled, string.IsNullOrWhiteSpace(reason) ? "Cancelled by the remote peer." : reason);
            return new PeerTransferCancellationAcknowledgement(requestId, terminal.State, terminal.TerminalReason);
        }

        public void ApplyCancellationAcknowledgement(PeerTransferCancellationAcknowledgement acknowledgement)
        {
            Guid localRequest;
            lock (operationGate)
            {
                localRequest = localCancellationRequestId;
            }

            if (acknowledgement.RequestId == Guid.Empty || acknowledgement.RequestId != localRequest)
            {
                return;
            }

            PeerTransferState terminalState = acknowledgement.TerminalState switch
            {
                PeerTransferState.Completed => PeerTransferState.Completed,
                PeerTransferState.Cancelled => PeerTransferState.Cancelled,
                _ => PeerTransferState.Failed
            };
            CompleteTerminal(terminalState, acknowledgement.Reason);
        }

        public async Task<PeerTransferSnapshot> QueryRemoteAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            PeerTransferSnapshot current = Snapshot;
            if (current.IsTerminal)
            {
                return current;
            }

            await queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TaskCompletionSource<PeerTransferSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (operationGate)
                {
                    remoteStatusCompletion = completion;
                }

                await owner.SendControlAsync(PeerTransferFrameKind.Query, Descriptor.TransferId, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transferCancellation.Token);
                linked.CancelAfter(timeout);
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the remote transfer status.");
            }
            finally
            {
                lock (operationGate)
                {
                    remoteStatusCompletion = null;
                }

                queryGate.Release();
            }
        }

        public void ApplyRemoteStatus(PeerTransferSnapshot remoteStatus)
        {
            if (remoteStatus.TransferId != Descriptor.TransferId || remoteStatus.SessionId != owner.options.SessionId)
            {
                return;
            }

            TaskCompletionSource<PeerTransferSnapshot>? completion;
            lock (operationGate)
            {
                completion = remoteStatusCompletion;
            }

            completion?.TrySetResult(remoteStatus);
        }

        public LocalCancellation BeginLocalCancellation(string reason)
        {
            PeerTransferSnapshot current = Snapshot;
            if (current.IsTerminal)
            {
                return new LocalCancellation(
                    Guid.Empty,
                    false,
                    Task.FromResult(current));
            }

            PeerTransferSnapshot? changed = null;
            Guid requestId;
            lock (operationGate)
            {
                if (localCancellationRequestId != Guid.Empty)
                {
                    return new LocalCancellation(localCancellationRequestId, false, terminalCompletion.Task);
                }

                localCancellationRequestId = Guid.NewGuid();
                requestId = localCancellationRequestId;
                snapshot = snapshot with
                {
                    State = PeerTransferState.Cancelling,
                    TerminalReason = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason,
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            owner.ReportTransferChanged(changed);
            return new LocalCancellation(requestId, true, terminalCompletion.Task);
        }

        private bool BeginSenderCompletion()
        {
            PeerTransferSnapshot? changed = null;
            lock (operationGate)
            {
                if (snapshot.IsTerminal || senderCompletionSent)
                {
                    return false;
                }

                if (snapshot.State is not (PeerTransferState.Streaming or PeerTransferState.Completing))
                {
                    throw new InvalidOperationException("The transfer is not ready for sender completion.");
                }

                senderCompletionSent = true;
                snapshot = snapshot with
                {
                    State = PeerTransferState.Completing,
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            owner.ReportTransferChanged(changed);
            return true;
        }

        private bool HasRemoteSenderCompleted()
        {
            lock (operationGate)
            {
                return remoteSenderCompleted;
            }
        }

        private void MoveToCompleting()
        {
            PeerTransferSnapshot? changed = null;
            lock (operationGate)
            {
                if (snapshot.IsTerminal || snapshot.State == PeerTransferState.Completing)
                {
                    return;
                }

                snapshot = snapshot with
                {
                    State = PeerTransferState.Completing,
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            owner.ReportTransferChanged(changed);
        }

        private void EnsureStreaming()
        {
            PeerTransferSnapshot current = Snapshot;
            if (current.State != PeerTransferState.Streaming)
            {
                throw new InvalidOperationException("The transfer is not streaming.");
            }
        }

        private void AddTransferredBytes(int count)
        {
            if (count == 0)
            {
                return;
            }

            PeerTransferSnapshot? changed = null;
            lock (operationGate)
            {
                if (snapshot.IsTerminal)
                {
                    return;
                }

                snapshot = snapshot with
                {
                    BytesTransferred = checked(snapshot.BytesTransferred + count),
                    Revision = snapshot.Revision + 1
                };
                changed = snapshot;
            }

            owner.ReportTransferChanged(changed);
        }

        private PeerTransferSnapshot CompleteTerminal(PeerTransferState terminalState, string reason)
        {
            PeerTransferSnapshot terminal;
            lock (operationGate)
            {
                if (snapshot.IsTerminal)
                {
                    return snapshot;
                }

                terminal = snapshot with
                {
                    State = terminalState,
                    TerminalReason = terminalState == PeerTransferState.Completed ? string.Empty : reason ?? string.Empty,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Revision = snapshot.Revision + 1
                };
                snapshot = terminal;
            }

            if (terminalState == PeerTransferState.Completed)
            {
                inboundData.Writer.TryComplete();
            }
            else
            {
                inboundData.Writer.TryComplete(new OperationCanceledException(terminal.TerminalReason));
            }

            transferCancellation.Cancel();
            terminalCompletion.TrySetResult(terminal);
            openAcknowledgement.TrySetResult(new PeerTransferOpenAcknowledgement(
                terminalState == PeerTransferState.Completed,
                terminal.TerminalReason));
            owner.ReportTransferChanged(terminal);
            return terminal;
        }

        private static void ThrowIfNotCompleted(PeerTransferSnapshot terminal)
        {
            if (terminal.State == PeerTransferState.Completed)
            {
                return;
            }

            if (terminal.State == PeerTransferState.Cancelled)
            {
                throw new OperationCanceledException(string.IsNullOrWhiteSpace(terminal.TerminalReason)
                    ? "The peer transfer was cancelled."
                    : terminal.TerminalReason);
            }

            throw new IOException(string.IsNullOrWhiteSpace(terminal.TerminalReason)
                ? "The peer transfer failed."
                : terminal.TerminalReason);
        }
    }
}