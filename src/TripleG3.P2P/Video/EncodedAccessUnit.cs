using System.Buffers;
namespace TripleG3.P2P.Video;

/// <summary>
/// Represents a complete encoded video access unit (frame) in Annex B format (start codes present).
/// Timestamp90k: RTP 90kHz clock timestamp associated with this frame.
/// CaptureTicks: raw capture stopwatch ticks for latency measurements.
/// </summary>
public readonly record struct EncodedAccessUnit(ReadOnlyMemory<byte> AnnexB, bool IsKeyFrame, uint Timestamp90k, long CaptureTicks, IPooledFrame? Pooled = null) : IDisposable
{
	public void Dispose() => Pooled?.Dispose();
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
