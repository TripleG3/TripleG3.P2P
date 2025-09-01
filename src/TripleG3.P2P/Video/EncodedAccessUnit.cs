using System.Buffers;
namespace TripleG3.P2P.Video;

/// <summary>
/// Stable public representation of one encoded video frame (Annex B access unit: start codes + NAL sequence).
/// Use <see cref="RtpTimestamp90k"/> for RTP clock time (90 kHz) and <see cref="CaptureTicks"/> for original capture ticks.
/// Disposing returns the pooled buffer (if owned). Double-dispose is safe/no-op.
/// </summary>
public readonly struct EncodedAccessUnit : IDisposable
{
	/// <summary>Annex B bytes (do not mutate).</summary>
	public ReadOnlyMemory<byte> AnnexB { get; }
	/// <summary>Whether this frame is a key frame (IDR / recovery point).</summary>
	public bool IsKeyFrame { get; }
	/// <summary>Associated RTP timestamp in the 90 kHz clock domain.</summary>
	public uint RtpTimestamp90k { get; }
	/// <summary>Original capture time in Stopwatch ticks (for latency measurement).</summary>
	public long CaptureTicks { get; }
	private readonly IPooledFrame? _pooled;

	// Legacy compatibility property still used internally by packetizer; keep name.
	internal uint Timestamp90k => RtpTimestamp90k;

	/// <summary>Create an access unit from an Annex B buffer (caller supplies timestamp/capture info).</summary>
	public EncodedAccessUnit(ReadOnlyMemory<byte> annexB, bool isKeyFrame, uint rtpTimestamp90k, long captureTicks)
	{
		AnnexB = annexB;
		IsKeyFrame = isKeyFrame;
		RtpTimestamp90k = rtpTimestamp90k;
		CaptureTicks = captureTicks;
		_pooled = null;
	}

	/// <summary>Internal constructor allowing a pooled frame wrapper.</summary>
	internal EncodedAccessUnit(IPooledFrame pooled, int length, bool isKeyFrame, uint rtpTimestamp90k, long captureTicks)
	{
		AnnexB = pooled.Memory.Slice(0, length);
		IsKeyFrame = isKeyFrame;
		RtpTimestamp90k = rtpTimestamp90k;
		CaptureTicks = captureTicks;
		_pooled = pooled;
	}

	/// <summary>Dispose returns the rented buffer (if any) to the shared pool.</summary>
	public void Dispose() => _pooled?.Dispose();

	/// <summary>Create from capture ticks (helper) computing RTP timestamp (90k) from wall clock ticks.</summary>
	public static EncodedAccessUnit FromAnnexB(ReadOnlyMemory<byte> annexB, long captureTicks, bool isKeyFrame)
	{
		uint ts90 = (uint)((captureTicks * 90000) / TimeSpan.TicksPerSecond);
		return new EncodedAccessUnit(annexB, isKeyFrame, ts90, captureTicks);
	}
}

/// <summary>Ownership wrapper for a pooled frame buffer.</summary>
public interface IPooledFrame : IDisposable
{
	Memory<byte> Memory { get; }
}

internal sealed class ArrayPoolFrame : IPooledFrame
{
	private byte[]? _buffer;
	private readonly int _length;
	public ArrayPoolFrame(int size)
	{
		_buffer = ArrayPool<byte>.Shared.Rent(size);
		_length = size;
	}
	public Memory<byte> Memory => _buffer == null ? Memory<byte>.Empty : _buffer.AsMemory(0, _length);
	public void Dispose()
	{
		var buf = _buffer;
		if (buf != null)
		{
			_buffer = null;
			ArrayPool<byte>.Shared.Return(buf);
		}
	}
}
