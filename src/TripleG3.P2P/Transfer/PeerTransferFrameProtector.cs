using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TripleG3.P2P.Transfer;

/// <summary>Protects post-handshake frames with independent authenticated encryption sequences per direction.</summary>
internal sealed class PeerTransferFrameProtector : IDisposable
{
    public const int AuthenticationOverheadBytes = 16;
    private readonly AesGcm outboundCipher;
    private readonly AesGcm inboundCipher;
    private readonly byte[] key;
    private readonly byte[] outboundNoncePrefix;
    private readonly byte[] inboundNoncePrefix;
    private ulong outboundSequence;
    private ulong inboundSequence;
    private int disposed;

    public PeerTransferFrameProtector(PeerTransferSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        key = SHA256.HashData(Encoding.UTF8.GetBytes(options.SessionGrant));
        outboundCipher = new AesGcm(key, AuthenticationOverheadBytes);
        inboundCipher = new AesGcm(key, AuthenticationOverheadBytes);
        outboundNoncePrefix = CreateNoncePrefix(options.SessionId, options.LocalDeviceId, options.RemoteDeviceId);
        inboundNoncePrefix = CreateNoncePrefix(options.SessionId, options.RemoteDeviceId, options.LocalDeviceId);
    }

    public ReadOnlyMemory<byte> Protect(PeerTransferFrame frame)
    {
        ThrowIfDisposed();
        byte[] protectedPayload = new byte[frame.Payload.Length + AuthenticationOverheadBytes];
        Span<byte> cipherText = protectedPayload.AsSpan(0, frame.Payload.Length);
        Span<byte> authenticationTag = protectedPayload.AsSpan(frame.Payload.Length, AuthenticationOverheadBytes);
        byte[] nonce = CreateNonce(outboundNoncePrefix, NextOutboundSequence());
        byte[] authenticationData = PeerTransferWireProtocol.CreateAuthenticationData(
            frame.Kind,
            frame.SessionId,
            frame.TransferId,
            protectedPayload.Length);
        outboundCipher.Encrypt(nonce, frame.Payload.Span, cipherText, authenticationTag, authenticationData);
        return protectedPayload;
    }

    public ReadOnlyMemory<byte> Unprotect(PeerTransferFrame frame)
    {
        ThrowIfDisposed();
        if (frame.Payload.Length < AuthenticationOverheadBytes)
        {
            throw new CryptographicException("The protected peer transfer frame is missing its authentication tag.");
        }

        int plainTextLength = frame.Payload.Length - AuthenticationOverheadBytes;
        byte[] plainText = new byte[plainTextLength];
        ReadOnlySpan<byte> cipherText = frame.Payload.Span[..plainTextLength];
        ReadOnlySpan<byte> authenticationTag = frame.Payload.Span[plainTextLength..];
        byte[] nonce = CreateNonce(inboundNoncePrefix, NextInboundSequence());
        byte[] authenticationData = PeerTransferWireProtocol.CreateAuthenticationData(
            frame.Kind,
            frame.SessionId,
            frame.TransferId,
            frame.Payload.Length);
        inboundCipher.Decrypt(nonce, cipherText, authenticationTag, plainText, authenticationData);
        return plainText;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        outboundCipher.Dispose();
        inboundCipher.Dispose();
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(outboundNoncePrefix);
        CryptographicOperations.ZeroMemory(inboundNoncePrefix);
    }

    private ulong NextOutboundSequence()
    {
        if (outboundSequence == ulong.MaxValue)
        {
            throw new CryptographicException("The peer transfer outbound encryption sequence is exhausted.");
        }

        return outboundSequence++;
    }

    private ulong NextInboundSequence()
    {
        if (inboundSequence == ulong.MaxValue)
        {
            throw new CryptographicException("The peer transfer inbound encryption sequence is exhausted.");
        }

        return inboundSequence++;
    }

    private static byte[] CreateNoncePrefix(Guid sessionId, string senderDeviceId, string receiverDeviceId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:D}|{senderDeviceId}|{receiverDeviceId}"));
        return hash[..4];
    }

    private static byte[] CreateNonce(byte[] prefix, ulong sequence)
    {
        byte[] nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}