using System.Security.Cryptography;

namespace TripleG3.P2P.Hubs;

/// <summary>Maps one monotonic capture timeline to synchronized RTP audio and video clock domains.</summary>
public sealed class RtpMediaClock
{
    public RtpMediaClock(
        long captureOrigin,
        long captureFrequency,
        uint? audioOrigin48k = null,
        uint? videoOrigin90k = null)
    {
        if (captureFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(captureFrequency));
        CaptureOrigin = captureOrigin;
        CaptureFrequency = captureFrequency;
        AudioOrigin48k = audioOrigin48k ?? CreateRandomOrigin();
        VideoOrigin90k = videoOrigin90k ?? CreateRandomOrigin();
    }

    public long CaptureOrigin { get; }

    public long CaptureFrequency { get; }

    public uint AudioOrigin48k { get; }

    public uint VideoOrigin90k { get; }

    public RtpMediaTimestamps Map(long captureTimestamp)
    {
        if (captureTimestamp < CaptureOrigin) throw new ArgumentOutOfRangeException(nameof(captureTimestamp));
        var delta = (Int128)captureTimestamp - CaptureOrigin;
        var audioDelta = delta * 48_000 / CaptureFrequency;
        var videoDelta = delta * 90_000 / CaptureFrequency;
        return new RtpMediaTimestamps(
            unchecked(AudioOrigin48k + (uint)audioDelta),
            unchecked(VideoOrigin90k + (uint)videoDelta));
    }

    private static uint CreateRandomOrigin()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }
}