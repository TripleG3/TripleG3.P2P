using System.Net;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.Audio;

/// <summary>Configuration for an initial 48 kHz mono Opus RTP stream.</summary>
public sealed class RtpAudioConfig
{
    public IPAddress LocalAddress { get; init; } = IPAddress.Any;

    public int LocalPort { get; init; }

    public required IPEndPoint RemoteEndPoint { get; init; }

    public uint Ssrc { get; init; }

    public int PayloadType { get; init; } = 111;

    public int SampleRate { get; init; } = 48_000;

    public int Channels { get; init; } = 1;

    public TimeSpan PacketDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    public int MaximumQueuedFrames { get; init; } = 32;

    public IPeerAuthorizer? PeerAuthorizer { get; init; }

    public string? SessionId { get; init; }

    public string? SenderDeviceId { get; init; }
}