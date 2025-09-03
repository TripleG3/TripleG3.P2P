namespace TripleG3.P2P.Video;

/// <summary>
/// Minimal payload cipher abstraction for the stable RTP surface. Encrypt/Decrypt may operate in-place.
/// Implementations return the resulting payload length. OverheadBytes signals additional bytes that might be appended.
/// </summary>
public interface IVideoPayloadCipher
{
	/// <summary>Static overhead (bytes) this cipher may add (e.g., auth tag).</summary>
	int OverheadBytes { get; }
	/// <summary>Encrypt the buffer contents in-place. Return new length (may be unchanged).</summary>
	int Encrypt(Span<byte> buffer);
	/// <summary>Decrypt the buffer contents in-place. Return new length (may be unchanged).</summary>
	int Decrypt(Span<byte> buffer);
}

/// <summary>No-op cipher (test / placeholder only).</summary>
public sealed class NoOpCipher : IVideoPayloadCipher
{
	public int OverheadBytes => 0;
	public int Encrypt(Span<byte> buffer) => buffer.Length;
	public int Decrypt(Span<byte> buffer) => buffer.Length;
}