namespace TripleG3.P2P.Security
{
    public interface ICipher
    {
        int OverheadBytes { get; }
        int Encrypt(Span<byte> buffer);
        int Decrypt(Span<byte> buffer);

        int Encrypt(ReadOnlySpan<byte> payload, Span<byte> output)
        {
            if (OverheadBytes != 0)
            {
                throw new NotSupportedException("Ciphers with overhead must implement the input/output Encrypt overload.");
            }

            payload.CopyTo(output);
            return Encrypt(output[..payload.Length]);
        }

        int Decrypt(ReadOnlySpan<byte> payload, Span<byte> output)
        {
            payload.CopyTo(output);
            return Decrypt(output[..payload.Length]);
        }
    }
}
