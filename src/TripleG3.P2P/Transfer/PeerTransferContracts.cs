using System.Net;
using System.Net.Sockets;

namespace TripleG3.P2P.Transfer;

/// <summary>Describes the lifecycle of one reusable peer transfer session.</summary>
public enum PeerTransferSessionState
{
    None = 0,
    Connecting,
    Connected,
    Closing,
    Closed,
    Failed
}

/// <summary>Describes the lifecycle of one logical payload transfer within a session.</summary>
public enum PeerTransferState
{
    None = 0,
    Opening,
    Streaming,
    Completing,
    Cancelling,
    Cancelled,
    Completed,
    Failed
}

/// <summary>Identifies the payload contract handled by a logical transfer.</summary>
public enum PeerTransferKind
{
    None = 0,
    RawText,
    File
}

/// <summary>Configuration required to establish one authenticated reusable transfer session.</summary>
public sealed record PeerTransferSessionOptions(
    Guid SessionId,
    string LocalDeviceId,
    string RemoteDeviceId,
    string SessionGrant)
{
    /// <summary>Maximum wire-frame payload accepted before allocating the payload buffer.</summary>
    public int MaximumFrameBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum simultaneously active logical transfers in this session.</summary>
    public int MaximumConcurrentTransfers { get; init; } = 16;

    /// <summary>Maximum buffered inbound data frames per transfer.</summary>
    public int MaximumBufferedDataFramesPerTransfer { get; init; } = 16;

    /// <summary>Maximum queued outbound data frames for the complete session.</summary>
    public int MaximumQueuedDataFrames { get; init; } = 128;

    /// <summary>Maximum accepted UTF-8 control metadata payload.</summary>
    public int MaximumControlPayloadBytes { get; init; } = 8 * 1024;

    /// <summary>Maximum time allowed to complete the mutual session handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum time to wait for a transfer cancellation acknowledgement.</summary>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Interval between transport-owned liveness probes while the session is idle.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum inbound inactivity before the transport marks the session failed.</summary>
    public TimeSpan SessionInactivityTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Host-owned authorization for the remote device and opaque session grant.</summary>
    public IPeerTransferSessionAuthorizer? Authorizer { get; init; }

    public void Validate()
    {
        if (SessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(SessionId));
        if (string.IsNullOrWhiteSpace(LocalDeviceId)) throw new ArgumentException("A local device identifier is required.", nameof(LocalDeviceId));
        if (string.IsNullOrWhiteSpace(RemoteDeviceId)) throw new ArgumentException("A remote device identifier is required.", nameof(RemoteDeviceId));
        if (string.Equals(LocalDeviceId, RemoteDeviceId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A peer transfer session requires different local and remote devices.", nameof(RemoteDeviceId));
        if (string.IsNullOrWhiteSpace(SessionGrant)) throw new ArgumentException("A session grant is required.", nameof(SessionGrant));
        if (MaximumFrameBytes is < 256 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumFrameBytes));
        if (MaximumConcurrentTransfers is < 1 or > 1_024) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentTransfers));
        if (MaximumBufferedDataFramesPerTransfer is < 1 or > 1_024) throw new ArgumentOutOfRangeException(nameof(MaximumBufferedDataFramesPerTransfer));
        if (MaximumQueuedDataFrames is < 1 or > 16_384) throw new ArgumentOutOfRangeException(nameof(MaximumQueuedDataFrames));
        if (MaximumControlPayloadBytes < 256 || MaximumControlPayloadBytes > MaximumFrameBytes - 16) throw new ArgumentOutOfRangeException(nameof(MaximumControlPayloadBytes));
        if (HandshakeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));
        if (ControlTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ControlTimeout));
        if (HeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
        if (SessionInactivityTimeout <= HeartbeatInterval) throw new ArgumentOutOfRangeException(nameof(SessionInactivityTimeout));
    }
}

/// <summary>Bounded authentication context exchanged before any transfer can open.</summary>
public sealed record PeerTransferSessionHello(
    Guid SessionId,
    string SenderDeviceId,
    string ReceiverDeviceId,
    string SessionGrant);

/// <summary>Host authorization result for a remote transfer-session handshake.</summary>
public sealed record PeerTransferSessionAuthorization(bool Accepted, string Reason)
{
    public static PeerTransferSessionAuthorization Allow() => new(true, string.Empty);

    public static PeerTransferSessionAuthorization Deny(string reason) => new(false, reason ?? string.Empty);
}

/// <summary>Host-owned policy that validates the peer identity and opaque session grant.</summary>
public interface IPeerTransferSessionAuthorizer
{
    ValueTask<PeerTransferSessionAuthorization> AuthorizeAsync(
        PeerTransferSessionHello hello,
        CancellationToken cancellationToken);
}

/// <summary>Metadata that identifies one payload operation without carrying payload bytes.</summary>
public sealed record PeerTransferDescriptor(
    Guid TransferId,
    PeerTransferKind Kind,
    string DisplayName,
    string Summary,
    long ExpectedLength,
    string IntegrityHash)
{
    public static PeerTransferDescriptor Empty { get; } = new(
        Guid.Empty,
        PeerTransferKind.None,
        string.Empty,
        string.Empty,
        0,
        string.Empty);

    /// <summary>Optional host-defined payload classification used only for handler selection and presentation.</summary>
    public string PayloadType { get; init; } = string.Empty;

    public bool IsValid =>
        TransferId != Guid.Empty &&
        Kind != PeerTransferKind.None &&
        ExpectedLength >= 0 &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        !string.IsNullOrWhiteSpace(Summary);
}

/// <summary>Immutable transfer lifecycle projection suitable for host state services.</summary>
public sealed record PeerTransferSnapshot(
    Guid SessionId,
    Guid TransferId,
    PeerTransferKind Kind,
    PeerTransferState State,
    string DisplayName,
    string Summary,
    long ExpectedLength,
    long BytesTransferred,
    string IntegrityHash,
    string TerminalReason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long Revision)
{
    public static PeerTransferSnapshot Empty { get; } = new(
        Guid.Empty,
        Guid.Empty,
        PeerTransferKind.None,
        PeerTransferState.None,
        string.Empty,
        string.Empty,
        0,
        0,
        string.Empty,
        string.Empty,
        default,
        default,
        0);

    public bool IsTerminal => State is PeerTransferState.Cancelled or PeerTransferState.Completed or PeerTransferState.Failed;
}

/// <summary>Immutable session lifecycle projection suitable for host state services.</summary>
public sealed record PeerTransferSessionSnapshot(
    Guid SessionId,
    string LocalDeviceId,
    string RemoteDeviceId,
    PeerTransferSessionState State,
    string Message,
    DateTimeOffset ChangedAt,
    long Revision)
{
    public static PeerTransferSessionSnapshot Empty { get; } = new(
        Guid.Empty,
        string.Empty,
        string.Empty,
        PeerTransferSessionState.None,
        string.Empty,
        default,
        0);
}

/// <summary>Reply to an inbound transfer-open request.</summary>
public sealed record PeerTransferDecision(bool Accepted, string Reason)
{
    public static PeerTransferDecision Accept() => new(true, string.Empty);

    public static PeerTransferDecision Reject(string reason) => new(false, reason ?? string.Empty);
}

/// <summary>Provides the accepted inbound transfer to a host payload handler.</summary>
public sealed record PeerTransferOpenRequest(
    PeerTransferDescriptor Descriptor,
    IPeerTransfer Transfer);

/// <summary>Event payload for a session lifecycle transition.</summary>
public sealed record PeerTransferSessionStateChangedEventArgs(PeerTransferSessionSnapshot Snapshot);

/// <summary>Event payload for a transfer lifecycle transition.</summary>
public sealed record PeerTransferStateChangedEventArgs(PeerTransferSnapshot Snapshot);

/// <summary>One bidirectional P2P session that can carry multiple independent logical transfers.</summary>
public interface IPeerTransferSession : IAsyncDisposable
{
    PeerTransferSessionSnapshot Snapshot { get; }

    IReadOnlyList<PeerTransferSnapshot> Transfers { get; }

    event EventHandler<PeerTransferSessionStateChangedEventArgs>? StateChanged;

    event EventHandler<PeerTransferStateChangedEventArgs>? TransferChanged;

    event Func<PeerTransferOpenRequest, CancellationToken, ValueTask<PeerTransferDecision>>? TransferRequested;

    /// <summary>
    /// Raised after the remote peer accepts an inbound transfer. Handlers must begin payload processing
    /// without blocking the session receive callback.
    /// </summary>
    event Action<IPeerTransfer>? InboundTransferOpened;

    Task<IPeerTransfer> OpenTransferAsync(PeerTransferDescriptor descriptor, CancellationToken cancellationToken = default);

    Task<PeerTransferSnapshot> QueryTransferAsync(Guid transferId, CancellationToken cancellationToken = default);

    Task CancelTransferAsync(Guid transferId, string reason, CancellationToken cancellationToken = default);

    bool TryGetTransfer(Guid transferId, out PeerTransferSnapshot snapshot);

    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Access to one logical transfer's stream-like payload and terminal lifecycle.</summary>
public interface IPeerTransfer
{
    PeerTransferSnapshot Snapshot { get; }

    bool IsSender { get; }

    int MaximumDataFrameBytes { get; }

    CancellationToken TransferCancellationToken { get; }

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(string reason, CancellationToken cancellationToken = default);

    Task FailAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>Creates client and accepted-server instances of reusable TCP transfer sessions.</summary>
public interface IPeerTransferSessionFactory
{
    Task<IPeerTransferSession> ConnectAsync(
        IPEndPoint remoteEndPoint,
        PeerTransferSessionOptions options,
        CancellationToken cancellationToken = default);

    Task<IPeerTransferSession> AcceptAsync(
        TcpClient client,
        PeerTransferSessionOptions options,
        CancellationToken cancellationToken = default);
}