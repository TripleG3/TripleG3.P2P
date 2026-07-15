using System.Buffers;
using System.Diagnostics;

namespace TripleG3.P2P.Video.Rtp;

internal sealed class H264FrameAssembly
{
    private readonly List<(byte[] Buffer, int Length)> _nalUnits = [];
    private byte[]? _fuBuffer;
    private int _fuLength;
    private ushort? _expectedSequence;

    public long CreatedTimestamp { get; } = Stopwatch.GetTimestamp();

    public bool Invalid { get; set; }

    public bool IsKeyFrame { get; private set; }

    public bool HasCurrentFu => _fuBuffer is not null;

    public int NalCount => _nalUnits.Count;

    public int TotalNalBytes { get; private set; }

    public int AnnexBLength => TotalNalBytes + _nalUnits.Count * 4;

    public bool AcceptSequence(ushort sequenceNumber)
    {
        if (_expectedSequence.HasValue && sequenceNumber != _expectedSequence.Value) return false;
        _expectedSequence = unchecked((ushort)(sequenceNumber + 1));
        return true;
    }

    public bool StartFu(byte reconstructedHeader, int maximumNalBytes, int maximumFrameBytes)
    {
        if (_fuBuffer is not null || maximumNalBytes < 1 || TotalNalBytes + 1 > maximumFrameBytes) return false;
        _fuBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(2048, maximumNalBytes));
        _fuBuffer[0] = reconstructedHeader;
        _fuLength = 1;
        return true;
    }

    public bool AppendFuPayload(
        ReadOnlySpan<byte> fragment,
        int maximumNalBytes,
        int maximumFrameBytes)
    {
        if (_fuBuffer is null) return false;
        var requiredLength = _fuLength + fragment.Length;
        if (requiredLength > maximumNalBytes || TotalNalBytes + requiredLength > maximumFrameBytes) return false;
        EnsureFuCapacity(requiredLength, maximumNalBytes);
        fragment.CopyTo(_fuBuffer.AsSpan(_fuLength));
        _fuLength = requiredLength;
        return true;
    }

    public bool CompleteFu(byte nalType)
    {
        if (_fuBuffer is null) return false;
        _nalUnits.Add((_fuBuffer, _fuLength));
        TotalNalBytes += _fuLength;
        IsKeyFrame |= nalType == 5;
        _fuBuffer = null;
        _fuLength = 0;
        return true;
    }

    public bool AddSingleNal(ReadOnlySpan<byte> payload, int maximumNalBytes, int maximumFrameBytes)
    {
        if (payload.Length == 0 || payload.Length > maximumNalBytes || TotalNalBytes + payload.Length > maximumFrameBytes)
        {
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        _nalUnits.Add((buffer, payload.Length));
        TotalNalBytes += payload.Length;
        IsKeyFrame |= (payload[0] & 0x1F) == 5;
        return true;
    }

    public void CopyAnnexBTo(Span<byte> destination)
    {
        if (destination.Length < AnnexBLength) throw new ArgumentException("Destination is too small.", nameof(destination));
        var offset = 0;
        foreach (var nalUnit in _nalUnits)
        {
            destination[offset++] = 0;
            destination[offset++] = 0;
            destination[offset++] = 0;
            destination[offset++] = 1;
            nalUnit.Buffer.AsSpan(0, nalUnit.Length).CopyTo(destination[offset..]);
            offset += nalUnit.Length;
        }
    }

    public void ReleaseAll()
    {
        foreach (var nalUnit in _nalUnits)
        {
            ArrayPool<byte>.Shared.Return(nalUnit.Buffer);
        }

        _nalUnits.Clear();
        TotalNalBytes = 0;
        if (_fuBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_fuBuffer);
            _fuBuffer = null;
        }

        _fuLength = 0;
    }

    private void EnsureFuCapacity(int requiredLength, int maximumNalBytes)
    {
        if (_fuBuffer is null || requiredLength <= _fuBuffer.Length) return;
        var newLength = Math.Min(maximumNalBytes, Math.Max(requiredLength, _fuBuffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(newLength);
        _fuBuffer.AsSpan(0, _fuLength).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_fuBuffer);
        _fuBuffer = replacement;
    }
}