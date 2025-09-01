# TripleG3.P2P.Video

Minimal, DI-friendly RTP video API consumed by `TripleG3.Camera.Maui` (no reflection required). Provides a tiny, stable surface while retaining the richer legacy implementation internally for compatibility.

## Stable Public Surface (Semantic-Stable)

Namespace: `TripleG3.P2P.Video`

```csharp
public readonly struct EncodedAccessUnit : IDisposable {
	ReadOnlyMemory<byte> AnnexB { get; }
	bool IsKeyFrame { get; }
	uint RtpTimestamp90k { get; }   // 90 kHz RTP clock
	long CaptureTicks { get; }      // original capture ticks (DateTime.UtcNow.Ticks or Stopwatch ticks)
}

public interface IVideoPayloadCipher {
	int OverheadBytes { get; }
	void Encrypt(Span<byte> payload);   // in-place
	void Decrypt(Span<byte> payload);   // in-place
}

public sealed class NoOpCipher : IVideoPayloadCipher { ... }

public sealed class RtpVideoSender : IDisposable {
	RtpVideoSender(uint ssrc, int mtu, IVideoPayloadCipher cipher, Action<ReadOnlyMemory<byte>> rtpOut, Action<ReadOnlyMemory<byte>>? rtcpOut = null);
	void Send(EncodedAccessUnit au);              // sync helper (fire & forget)
	Task<bool> SendAsync(EncodedAccessUnit au);   // async path
	void SendSenderReport(uint rtpTimestamp);     // optional (delegates to legacy)
	RtpVideoSenderStats GetStats();
}

public sealed class RtpVideoReceiver : IDisposable {
	RtpVideoReceiver(IVideoPayloadCipher cipher);
	event Action<EncodedAccessUnit>? AccessUnitReceived;
	void ProcessRtp(ReadOnlySpan<byte> packet);   // feed inbound RTP datagram
	void ProcessRtcp(ReadOnlySpan<byte> packet);  // optional (delegates to legacy)
	byte[]? CreateReceiverReport(uint reporterSsrc);
	RtpVideoReceiverStats GetStats();
	void RequestKeyframe();                       // raises KeyframeNeeded internally
}

public sealed class RtpVideoSenderStats { uint PacketsSent; uint BytesSent; uint AUsSent; }
public sealed class RtpVideoReceiverStats { uint PacketsReceived; uint BytesReceived; uint PacketsLost; }

public static class Rtcp { static bool IsRtcpPacket(ReadOnlySpan<byte> datagram); }
```

Everything else (packetizer internals, jitter, RTT, extended RTCP metrics, test ciphers under `Video.Security`) is **not** part of the stable contract and may change without a version bump beyond patch.

## Usage (Basic)

Sender (e.g. encoder thread):
```csharp
var cipher = new TripleG3.P2P.Video.NoOpCipher();
var sender = new RtpVideoSender(ssrc: 0x1234u, mtu: 1200, cipher,
	rtpOut: packet => udpSocket.Send(packet.Span),
	rtcpOut: rtcp => udpSocket.Send(rtcp.Span));

// Build Annex B access unit (start codes + NALs)
using var au = new EncodedAccessUnit(annexBBytes, isKeyFrame, rtpTs90k, captureTicks);
sender.Send(au); // or await sender.SendAsync(au);
```

Receiver:
```csharp
var recv = new RtpVideoReceiver(new TripleG3.P2P.Video.NoOpCipher());
recv.AccessUnitReceived += au =>
{
	if (au.IsKeyFrame) Console.WriteLine("Key frame");
	// au.AnnexB contains full frame (copy or decode then dispose)
	au.Dispose(); // return pooled buffer
};

// For each UDP datagram containing RTP:
recv.ProcessRtp(rtpPayload);
```

Compute RTP timestamp (90 kHz) from capture ticks (DateTime.UtcNow.Ticks):
```csharp
uint rtpTs = (uint)((captureTicks * 90000) / TimeSpan.TicksPerSecond);
```

Or build via helper:
```csharp
var au = EncodedAccessUnit.FromAnnexB(annexB, captureTicks, isKeyFrame);
```

## Legacy / Compatibility Layer

The original, more feature-rich implementation (reordering, extended RTCP/jitter, experimental ciphers) remains under `TripleG3.P2P.Video.Rtp` & `TripleG3.P2P.Video.Security`. The stable classes internally delegate to those when constructed with the stable cipher interface via an internal adapter. Do **not** rely on types from `.Video.Rtp` for new consumers.

Two `NoOpCipher` classes exist:
- `TripleG3.P2P.Video.NoOpCipher` (stable interface)
- `TripleG3.P2P.Video.Security.NoOpCipher` (legacy interface)
Use the first for new code. The second is kept only for existing tests / legacy pathways.

## Memory & Disposal

`EncodedAccessUnit` may wrap a pooled buffer. Always dispose it after use (pattern: `using var au = ...`). If you need to retain frame bytes, copy them before disposal.

## Stats Philosophy

Stats are intentionally minimal (packet/byte/frame counters + loss). Higher-level quality metrics (jitter, RTT) were removed from the stable surface to keep API lean; those may still be computed internally and could reappear via an opt-in extension in a future minor version.

## Versioning Policy

The list in Stable Public Surface above defines the contract for 1.x. Additions will trigger a minor (1.y.0) bump; removals / breaking changes will require 2.0.

## Testing Ciphers

`Video.Security.XorTestCipher` is strictly for tests. It is **not** secure. For real encryption integrate a proper SRTP or end-to-end media security layer externally.

## Logging / DI

Logging uses `Microsoft.Extensions.Logging`. You can register the library via provided service collection extensions (see root project) or manually instantiate senders/receivers as shown.

## Contributing Guidelines

1. Do not expand the stable surface without discussion.
2. Prefer internal helpers + adapters over exposing legacy types.
3. Keep allocations minimal; return pooled buffers promptly.
4. Add/update tests for any behavioral changes touching stable types.

