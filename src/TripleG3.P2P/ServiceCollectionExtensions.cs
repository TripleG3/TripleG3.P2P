using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using TripleG3.P2P.Serialization;
using TripleG3.P2P.Udp;

namespace TripleG3.P2P;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddP2PUdp(this IServiceCollection services)
    {
        services.AddSingleton<IMessageSerializer, NoneMessageSerializer>();
        services.AddSingleton<IMessageSerializer, JsonRawMessageSerializer>();
        services.AddSingleton<IMessageSerializer, LengthPrefixedMessageSerializer>();
        services.AddSingleton<ISerialBus, UdpSerialBus>();
        return services;
    }

    public static IServiceCollection AddP2PHubs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IHubCatalog, HubCatalog>();
        return services;
    }
}
