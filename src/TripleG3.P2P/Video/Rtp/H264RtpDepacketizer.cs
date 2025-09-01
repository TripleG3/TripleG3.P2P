using System.Buffers;
using System.Buffers.Binary;
using TripleG3.P2P.Video.Security;
using Microsoft.Extensions.Logging;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Reassembles H264 Annex B access units from RTP packets (single NAL + FU-A).</summary>
public sealed class H264RtpDepacketizer
{
    private readonly Dictionary<uint, FrameAssembly> _frames = new(); // timestamp -> assembly
    private readonly IVideoPayloadCipher _cipher;
    private readonly ILogger<H264RtpDepacketizer>? _log;
    private readonly int _maxFrames = 32;

    public H264RtpDepacketizer(IVideoPayloadCipher cipher, ILogger<H264RtpDepacketizer>? log = null) { _cipher = cipher; _log = log; }

    /// <summary>Process raw RTP datagram. If a complete frame is assembled returns true with AU.</summary>
    public bool TryProcessPacket(ReadOnlySpan<byte> datagram, out EncodedAccessUnit au)
    {
        au = default;
        try { _log?.LogDebug("H264RtpDepacketizer.TryProcessPacket len={Len}", datagram.Length); } catch { }
        if (!RtpPacket.TryParse(datagram, out var pkt)) return false;
        var meta = new RtpPacketMetadata(pkt.Timestamp, pkt.SequenceNumber, pkt.Ssrc, pkt.Marker);
        // Decrypt payload (in-place buffer)
        Span<byte> tmp = stackalloc byte[Math.Min(pkt.Payload.Length, 2048)]; // for small; larger allocate
        ReadOnlySpan<byte> payload = pkt.Payload.Length <= tmp.Length ? _cipher.Decrypt(meta, pkt.Payload, tmp) : DecryptLarge(meta, pkt.Payload);

        var assembly = GetOrCreateFrame(pkt.Timestamp);
        if (!ProcessNalFragment(assembly, payload))
        {
            assembly.Invalid = true;
        }
        if (pkt.Marker)
        {
            // finalize
            if (!assembly.Invalid && assembly.Nals.Count > 0)
            {
                var total = 0; foreach (var n in assembly.Nals) total += n.Length + 4; // start code
                var frame = new ArrayPoolFrame(total);
                var buffer = frame.Memory.Span;
                int offset = 0;
                foreach (var nal in assembly.Nals)
                {
                    buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 1;
                    nal.Span.CopyTo(buffer.Slice(offset, nal.Length));
                    offset += nal.Length;
                }
                bool isKey = assembly.IsKeyFrame;
                au = new EncodedAccessUnit(frame.Memory.Slice(0,total), isKey, pkt.Timestamp, 0, frame);
                // release intermediate NAL buffers now that consolidated frame allocated
                assembly.ReleaseAll();
                _frames.Remove(pkt.Timestamp);
                TrimIfNeeded();
                return true;
            }
            _frames.Remove(pkt.Timestamp); // drop invalid/incomplete
            TrimIfNeeded();
        }
        return false;
    }

    private ReadOnlySpan<byte> DecryptLarge(RtpPacketMetadata meta, ReadOnlySpan<byte> payload)
    {
        // Large payloads: allocate a dedicated buffer; copies avoided elsewhere dominate anyway.
        var arr = new byte[payload.Length];
        var span = arr.AsSpan();
        _cipher.Decrypt(meta, payload, span);
        return span;
    }

    private FrameAssembly GetOrCreateFrame(uint ts)
    {
        if (!_frames.TryGetValue(ts, out var fa)) { fa = new FrameAssembly(); _frames[ts] = fa; }
        return fa;
    }

    private bool ProcessNalFragment(FrameAssembly frame, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return false;
        byte nalHeader = payload[0];
        byte nalType = (byte)(nalHeader & 0x1F);
        if (nalType == 28) // FU-A
        {
            if (payload.Length < 2) return false;
            byte fuHeader = payload[1];
            bool start = (fuHeader & 0x80) != 0;
            bool end = (fuHeader & 0x40) != 0;
            byte origType = (byte)(fuHeader & 0x1F);
            byte nri = (byte)(nalHeader & 0x60);
            byte forbidden = (byte)(nalHeader & 0x80);
            if (start)
            {
                // Rent/allocate buffer for this FU assembly; we pessimistically size to MTU-ish growth via pooled list of segments.
                frame.StartNewFu((byte)(forbidden | nri | origType));
            }
            if (!frame.HasCurrentFu) return false; // missing start
            frame.AppendFuPayload(payload.Slice(2));
            if (end)
            {
                var nalSlice = frame.CompleteFu(out bool isIdr, origType);
                frame.Nals.Add(nalSlice);
                if (isIdr) frame.IsKeyFrame = true;
            }
            return true;
        }
        else
        {
            // Single NAL unit: copy once into pooled frame's final buffer later; here we just store an owned copy.
            var arr = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(arr);
            frame.Nals.Add(arr.AsMemory(0,payload.Length));
            frame.RentedBuffers.Add(arr);
            if (IsIdr(nalType)) frame.IsKeyFrame = true;
            return true;
        }
    }

    private static bool IsIdr(byte type) => type == 5; // simplistic

    private void TrimIfNeeded()
    {
        if (_frames.Count <= _maxFrames) return;
        // remove oldest
        var oldestTs = _frames.Keys.Min();
        _frames.Remove(oldestTs);
    }

    private sealed class FrameAssembly
    {
        public List<ReadOnlyMemory<byte>> Nals { get; } = new();
        public List<byte[]> RentedBuffers { get; } = new(); // for single NAL copies
        private byte[]? _fuBuffer; // current FU reassembly buffer
        private int _fuLength;
        public bool Invalid { get; set; }
        public bool IsKeyFrame { get; set; }
        public bool HasCurrentFu => _fuBuffer != null;

        public void StartNewFu(byte reconstructedHeader)
        {
            if (_fuBuffer != null) { ArrayPool<byte>.Shared.Return(_fuBuffer); _fuBuffer = null; }
            _fuBuffer = ArrayPool<byte>.Shared.Rent(2048); // initial size; will grow if needed
            _fuBuffer[0] = reconstructedHeader;
            _fuLength = 1;
        }

        public void AppendFuPayload(ReadOnlySpan<byte> fragment)
        {
            if (_fuBuffer == null) return;
            EnsureCapacity(_fuLength + fragment.Length);
            fragment.CopyTo(_fuBuffer.AsSpan(_fuLength));
            _fuLength += fragment.Length;
        }

        private void EnsureCapacity(int needed)
        {
            if (_fuBuffer == null) return;
            if (needed <= _fuBuffer.Length) return;
            var newSize = _fuBuffer.Length * 2;
            while (newSize < needed) newSize *= 2;
            var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
            _fuBuffer.AsSpan(0,_fuLength).CopyTo(newBuf);
            ArrayPool<byte>.Shared.Return(_fuBuffer);
            _fuBuffer = newBuf;
        }

        public ReadOnlyMemory<byte> CompleteFu(out bool isIdr, byte origType)
        {
            isIdr = origType == 5;
            var buf = _fuBuffer!;
            var mem = buf.AsMemory(0,_fuLength);
            RentedBuffers.Add(buf); // track for return after frame finalization
            _fuBuffer = null; _fuLength = 0;
            return mem;
        }

        public void ReleaseAll()
        {
            foreach (var b in RentedBuffers) ArrayPool<byte>.Shared.Return(b);
            RentedBuffers.Clear();
            if (_fuBuffer != null) { ArrayPool<byte>.Shared.Return(_fuBuffer); _fuBuffer = null; }
            _fuLength = 0;
        }
    }
}
