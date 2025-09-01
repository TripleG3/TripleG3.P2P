using System;
using System.Threading;
using System.Threading.Tasks;
using TripleG3.P2P.Video;

namespace TripleG3.P2P.Video.Abstractions
{
    public interface IRtpVideoReceiver : IKeyframeRequester, IDisposable
    {
    event Action<TripleG3.P2P.Video.EncodedAccessUnit?>? FrameReceived;

        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();
    }
}
