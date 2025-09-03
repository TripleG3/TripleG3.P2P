namespace TripleG3.P2P.Security
{
    public sealed class NoOpCipher : ICipher
    {
        public int OverheadBytes => 0;
        public int Encrypt(Span<byte> buffer) => buffer.Length;
        public int Decrypt(Span<byte> buffer) => buffer.Length;
    }
}
