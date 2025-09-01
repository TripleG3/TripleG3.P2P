using System;
using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Video.Abstractions;
using TripleG3.P2P.Video.Primitives;

namespace TripleG3.P2P.Video
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the TripleG3.P2P Video RTP services into the DI container.
        /// </summary>
        public static IServiceCollection AddTripleG3P2PVideo(this IServiceCollection services, Action<VideoP2POptions>? configure = null)
        {
            var opts = new VideoP2POptions();
            configure?.Invoke(opts);
            services.Add(new ServiceDescriptor(typeof(IRtpVideoSender), sp => new RtpVideoSender(new RtpVideoSenderConfig()), opts.SenderLifetime));
            services.Add(new ServiceDescriptor(typeof(IRtpVideoReceiver), sp => new RtpVideoReceiver(new RtpVideoReceiverConfig()), opts.ReceiverLifetime));
            return services;
        }
    }
}
