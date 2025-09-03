using Microsoft.Extensions.DependencyInjection;

namespace TripleG3.P2P.Video
{
    public sealed class VideoP2POptions
    {
        public ServiceLifetime SenderLifetime { get; set; } = ServiceLifetime.Transient;
        public ServiceLifetime ReceiverLifetime { get; set; } = ServiceLifetime.Transient;
    }
}
