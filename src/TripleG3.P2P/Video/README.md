# TripleG3.P2P.Video

Experimental, DI-friendly H.264 over RTP for .NET 10. The canonical public API is in `TripleG3.P2P.Video`; low-level packet types remain under `TripleG3.P2P.Video.Rtp`.

## Stable Entry Points

```csharp
public readonly struct EncodedAccessUnit : IDisposable
{
    ReadOnlyMemory<byte> AnnexB { get; }
    bool IsKeyFrame { get; }
    uint RtpTimestamp90k { get; }
    long CaptureTicks { get; }
}

public interface IVideoPayloadCipher
{
    int OverheadBytes { get; }
    int Encrypt(Span<byte> buffer);
    int Decrypt(Span<byte> buffer);
    int Encrypt(ReadOnlySpan<byte> payload, Span<byte> output);
    int Decrypt(ReadOnlySpan<byte> payload, Span<byte> output);
}

public sealed class RtpVideoSender : IDisposable
{
    RtpVideoSender(
        uint ssrc,
        int mtu,
        IVideoPayloadCipher cipher,
        Action<ReadOnlyMemory<byte>> rtpOut,
        Action<ReadOnlyMemory<byte>>? rtcpOut = null);

    void Send(EncodedAccessUnit accessUnit);
    Task<bool> SendAsync(EncodedAccessUnit accessUnit, CancellationToken cancellationToken = default);
    void SendSenderReport(uint rtpTimestamp);
    void ProcessRtcp(ReadOnlySpan<byte> packet);
    RtpVideoSenderStats GetStats();
}

public sealed class RtpVideoReceiver : IDisposable, IAsyncDisposable
{
    RtpVideoReceiver(IVideoPayloadCipher cipher);
    event Action<EncodedAccessUnit>? AccessUnitReceived;
    void ProcessRtp(ReadOnlySpan<byte> packet);
    void ProcessRtcp(ReadOnlySpan<byte> packet);
    byte[]? CreateReceiverReport(uint reporterSsrc);
    RtpVideoReceiverStats GetStats();
}
```

## Callback Transport

Use callback construction when the application owns UDP or another datagram transport:

```csharp
var cipher = new TripleG3.P2P.Video.NoOpCipher();
using var sender = new RtpVideoSender(
    ssrc: 0x1234u,
    mtu: 1200,
    cipher,
    rtpOut: packet => udpSocket.Send(packet.Span));

using var receiver = new RtpVideoReceiver(cipher);
receiver.AccessUnitReceived += accessUnit =>
{
    try
    {
        Decode(accessUnit.AnnexB.Span, accessUnit.IsKeyFrame);
    }
    finally
    {
        accessUnit.Dispose();
    }
};

using var frame = EncodedAccessUnit.FromAnnexB(annexB, captureTicks, isKeyFrame);
sender.Send(frame);
```

`Send` is available for callback-backed senders. Network-backed senders use `SendAsync` so cancellation and socket failure remain observable.

## Configured UDP and DI

DI requires explicit sender and receiver configuration. Resolving services with port-zero defaults is intentionally not supported.

```csharp
services.AddTripleG3P2PVideo(options =>
{
    options.SenderConfiguration = new RtpVideoSenderConfig
    {
        RemoteIp = "127.0.0.1",
        RemotePort = 7001,
        Ssrc = 0x1234,
        PayloadType = 96,
        Mtu = 1200
    };
    options.ReceiverConfiguration = new RtpVideoReceiverConfig
    {
        LocalAddress = IPAddress.Loopback,
        LocalPort = 7001,
        ExpectedSsrc = 0x1234,
        PayloadType = 96,
        JitterBufferMax = TimeSpan.FromMilliseconds(250)
    };
});

var sender = provider.GetRequiredService<IRtpVideoSender>();
var receiver = provider.GetRequiredService<IRtpVideoReceiver>();
await receiver.StartAsync(cancellationToken);
await sender.SendAsync(frame, cancellationToken);
await receiver.StopAsync();
```

The receiver constructor performs no asynchronous work. `StartAsync` is idempotent, `StopAsync` cancels and awaits the one receive loop, and the receiver may be restarted before disposal.

## Cipher Contract

The complete RTP payload is protected consistently for both single-NAL and FU-A packets. `OverheadBytes` is reserved inside the configured MTU. Ciphers that append authentication data must implement the separate input/output overloads; the default overload rejects nonzero overhead rather than silently truncating it.

`NoOpCipher` is a transport placeholder, not security. Production media security should use an authenticated design with managed key establishment, such as SRTP integrated by the application.

## Receiver Behavior

- Payload type is validated on every packet.
- `ExpectedSsrc` is enforced when configured; otherwise the first accepted SSRC is latched.
- RTP CSRC entries, header extensions, and padding are parsed before H.264 payload processing.
- Sequence numbers use circular 16-bit ordering and recover at bounded frame/window boundaries.
- A sequence gap invalidates the current H.264 frame but does not block later frames.
- In-flight frame count, frame bytes, NAL bytes, and assembly age are bounded.
- Every dropped or completed pooled assembly returns its buffers.

## Memory Ownership

Received `EncodedAccessUnit` instances may own pooled memory. Dispose each access unit after decoding. Copy `AnnexB` before disposal if it must outlive the callback.

Outbound RTP callbacks receive exact-length managed arrays and may retain them safely.

## Compatibility Layer

The old high-level `TripleG3.P2P.Video.Rtp.RtpVideoSender` and `RtpVideoReceiver` types are obsolete compatibility wrappers and are scheduled for removal in 2.0. New consumers should use `TripleG3.P2P.Video.RtpVideoSender`, `RtpVideoReceiver`, and the interfaces in `TripleG3.P2P.Video.Abstractions`.

Low-level `RtpPacket`, `H264RtpPacketizer`, `H264RtpDepacketizer`, and RTCP helpers remain implementation-oriented and may evolve with the experimental subsystem.

## Test Coverage

Unit tests cover deterministic in-process behavior:

- exact single-NAL and FU-A byte reconstruction;
- authenticated cipher overhead and tamper rejection;
- loss recovery and sequence-number wrap/concurrency;
- SSRC and payload-type filtering;
- bounded oversized-frame rejection;

Manual integration tests cover:

- receiver start, stop, and restart;
- complete DI resolution and UDP frame transfer;
- RTCP timing, negotiation round trips, and keyframe signaling.

```powershell
dotnet test tests/TripleG3.P2P.IntegrationTests/TripleG3.P2P.IntegrationTests.csproj -c Release -warnaserror
```
