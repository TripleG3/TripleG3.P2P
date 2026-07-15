using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Security;
using TripleG3.P2P.Video.Primitives;

namespace TripleG3.P2P.Video;

public sealed class VideoP2POptions
{
    public ServiceLifetime SenderLifetime { get; set; } = ServiceLifetime.Transient;

    public ServiceLifetime ReceiverLifetime { get; set; } = ServiceLifetime.Transient;

    public RtpVideoSenderConfig? SenderConfiguration { get; set; }

    public RtpVideoReceiverConfig? ReceiverConfiguration { get; set; }

    public Func<IServiceProvider, ICipher> CipherFactory { get; set; } = _ => new TripleG3.P2P.Security.NoOpCipher();
}