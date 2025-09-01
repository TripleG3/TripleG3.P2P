using System;

namespace TripleG3.P2P.Security
{
    public interface ICipher
    {
        int OverheadBytes { get; }
        int Encrypt(Span<byte> buffer);
        int Decrypt(Span<byte> buffer);
    }
}
