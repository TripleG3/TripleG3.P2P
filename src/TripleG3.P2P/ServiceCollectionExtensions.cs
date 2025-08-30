using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Core;
using TripleG3.P2P.Serialization;
using TripleG3.P2P.Udp;

namespace TripleG3.P2P;

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
