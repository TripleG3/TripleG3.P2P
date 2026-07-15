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

	/// <summary>Encrypts <paramref name="payload"/> into <paramref name="output"/> and returns the written length.</summary>
	int Encrypt(ReadOnlySpan<byte> payload, Span<byte> output)
	{
		if (OverheadBytes != 0)
		{
			throw new NotSupportedException("Ciphers with overhead must implement the input/output Encrypt overload.");
		}

		payload.CopyTo(output);
		return Encrypt(output[..payload.Length]);
	}

	/// <summary>Decrypts <paramref name="payload"/> into <paramref name="output"/> and returns the written length.</summary>
	int Decrypt(ReadOnlySpan<byte> payload, Span<byte> output)
	{
		payload.CopyTo(output);
		return Decrypt(output[..payload.Length]);
	}
}

/// <summary>No-op cipher (test / placeholder only).</summary>
public sealed class NoOpCipher : IVideoPayloadCipher
{
	public int OverheadBytes => 0;
	public int Encrypt(Span<byte> buffer) => buffer.Length;
	public int Decrypt(Span<byte> buffer) => buffer.Length;
}