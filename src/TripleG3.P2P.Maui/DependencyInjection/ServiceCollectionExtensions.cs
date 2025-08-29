using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.Serialization;
using TripleG3.P2P.Maui.Udp;

namespace TripleG3.P2P.Maui.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddP2PUdp(this IServiceCollection services)
    {
        services.AddSingleton<IMessageSerializer, NoneMessageSerializer>();
        services.AddSingleton<IMessageSerializer, JsonRawMessageSerializer>();
        services.AddSingleton<ISerialBus, UdpSerialBus>();
        return services;
    }
}
