using System.Net;

namespace TripleG3.P2P.Core;

/// <summary>Observable state of a P2P session or component.</summary>
public enum P2PSessionState
{
    Starting,
    Connected,
    Disconnected,
    Failed,
    Stopped
}

/// <summary>Describes a session lifecycle transition suitable for host state projection.</summary>
public sealed record P2PSessionStateChangedEventArgs(
    string SessionId,
    string? RemoteDeviceId,
    P2PSessionState State,
    string? Message = null);

/// <summary>Classifies a transport diagnostic reported by a P2P component.</summary>
public enum P2PDiagnosticKind
{
    ListenerStarted,
    ListenerStopped,
    PeerConnected,
    PeerDisconnected,
    AuthorizationRejected,
    MediaStreamOpened,
    MediaStreamClosed,
    FileTransferStarted,
    FileTransferProgress,
    FileTransferCompleted,
    FileTransferFailed,
    FileTransferCancelled,
    RtpStatisticsUpdated,
    KeyframeRequestReceived
}

/// <summary>Structured diagnostic event emitted by transport components.</summary>
public sealed record P2PDiagnosticEventArgs(
    P2PDiagnosticKind Kind,
    IPEndPoint? Peer = null,
    string? SessionId = null,
    string? Message = null,
    Guid? TransferId = null);