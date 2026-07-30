namespace TripleG3.P2P.Audio;

/// <summary>Receives 48 kHz mono Opus frames from RTP packets.</summary>
public interface IRtpAudioReceiver : IAsyncDisposable
{
    event Action<ReceivedAudioFrame>? AudioFrameReceived;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}