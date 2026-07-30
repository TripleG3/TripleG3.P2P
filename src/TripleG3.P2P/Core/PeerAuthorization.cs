using System.Net;

namespace TripleG3.P2P.Core;

/// <summary>Describes the P2P resource for which a remote peer is requesting access.</summary>
public enum P2PResourceKind
{
    SerialMessage,
    TcpConnection,
    RtpVideo,
    RtpAudio,
    FileTransfer
}

/// <summary>Context supplied to the host-owned peer authorization policy.</summary>
public sealed record PeerAuthorizationContext(
    string? SessionId,
    string? SenderDeviceId,
    IPEndPoint Peer,
    P2PResourceKind ResourceKind);

/// <summary>
/// Authorizes an inbound P2P resource. Implementations are supplied by the host and should use an
/// approved session/device allowlist rather than endpoint identity alone.
/// </summary>
public interface IPeerAuthorizer
{
    ValueTask<bool> AuthorizeAsync(PeerAuthorizationContext context, CancellationToken cancellationToken);
}