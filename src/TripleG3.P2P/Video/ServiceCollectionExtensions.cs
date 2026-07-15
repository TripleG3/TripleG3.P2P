using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TripleG3.P2P.Video.Abstractions;

namespace TripleG3.P2P.Video;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers configured RTP video sender and receiver abstractions.</summary>
    public static IServiceCollection AddTripleG3P2PVideo(
        this IServiceCollection services,
        Action<VideoP2POptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new VideoP2POptions();
        configure(options);
        var senderConfiguration = options.SenderConfiguration
            ?? throw new InvalidOperationException("Video sender configuration is required.");
        var receiverConfiguration = options.ReceiverConfiguration
            ?? throw new InvalidOperationException("Video receiver configuration is required.");
        ArgumentNullException.ThrowIfNull(options.CipherFactory);

        services.Add(new ServiceDescriptor(
            typeof(IRtpVideoSender),
            serviceProvider => new RtpVideoSender(
                senderConfiguration,
                options.CipherFactory(serviceProvider),
                serviceProvider.GetService<ILogger<RtpVideoSender>>()),
            options.SenderLifetime));
        services.Add(new ServiceDescriptor(
            typeof(IRtpVideoReceiver),
            serviceProvider => new RtpVideoReceiver(
                receiverConfiguration,
                options.CipherFactory(serviceProvider),
                serviceProvider.GetService<ILogger<RtpVideoReceiver>>()),
            options.ReceiverLifetime));
        return services;
    }
}