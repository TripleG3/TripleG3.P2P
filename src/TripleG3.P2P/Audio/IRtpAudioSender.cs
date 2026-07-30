namespace TripleG3.P2P.Audio;

/// <summary>Sends 48 kHz mono Opus frames in RTP packets.</summary>
public interface IRtpAudioSender : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> opusFrame, AudioFrameMetadata metadata, CancellationToken cancellationToken = default);
}