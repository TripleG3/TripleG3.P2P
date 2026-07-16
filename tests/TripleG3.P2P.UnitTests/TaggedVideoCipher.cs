namespace TripleG3.P2P.UnitTests;

internal sealed class TaggedVideoCipher(byte key = 0x5A) : TripleG3.P2P.Video.IVideoPayloadCipher
{
    private static readonly byte[] Tag = [0x54, 0x47, 0x33, 0x21];

    public int OverheadBytes => Tag.Length;

    public int Encrypt(Span<byte> buffer)
        => throw new NotSupportedException("Use the input/output overload for an expanding cipher.");

    public int Decrypt(Span<byte> buffer)
    {
        if (buffer.Length < Tag.Length || !buffer[^Tag.Length..].SequenceEqual(Tag))
        {
            throw new InvalidDataException("Video payload authentication failed.");
        }

        var plaintextLength = buffer.Length - Tag.Length;
        for (var index = 0; index < plaintextLength; index++) buffer[index] ^= key;
        return plaintextLength;
    }

    public int Encrypt(ReadOnlySpan<byte> payload, Span<byte> output)
    {
        if (output.Length < payload.Length + Tag.Length) throw new ArgumentException("Output is too small.", nameof(output));
        for (var index = 0; index < payload.Length; index++) output[index] = (byte)(payload[index] ^ key);
        Tag.CopyTo(output[payload.Length..]);
        return payload.Length + Tag.Length;
    }

    public int Decrypt(ReadOnlySpan<byte> payload, Span<byte> output)
    {
        if (payload.Length < Tag.Length || !payload[^Tag.Length..].SequenceEqual(Tag))
        {
            throw new InvalidDataException("Video payload authentication failed.");
        }

        var plaintextLength = payload.Length - Tag.Length;
        if (output.Length < plaintextLength) throw new ArgumentException("Output is too small.", nameof(output));
        for (var index = 0; index < plaintextLength; index++) output[index] = (byte)(payload[index] ^ key);
        return plaintextLength;
    }
}