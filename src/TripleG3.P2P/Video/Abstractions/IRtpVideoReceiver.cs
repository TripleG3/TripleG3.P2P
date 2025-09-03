namespace TripleG3.P2P.Video.Abstractions
{
    public interface IRtpVideoReceiver : IKeyframeRequester, IDisposable
    {
    event Action<TripleG3.P2P.Video.EncodedAccessUnit?>? FrameReceived;

        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();
    }
}
