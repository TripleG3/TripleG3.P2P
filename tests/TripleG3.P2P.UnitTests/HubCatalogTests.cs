using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class HubCatalogTests
{
    [Fact]
    public void Catalog_Creates_Finds_And_Removes_Independent_Hubs()
    {
        var catalog = new HubCatalog();
        var chatId = Guid.NewGuid();
        var hostedId = Guid.NewGuid();
        var gamingId = Guid.NewGuid();
        var host = Guid.NewGuid();

        var chat = catalog.CreateChatHub(chatId);
        var hosted = catalog.CreateHostedChatHub(hostedId, host, "Host");
        var gaming = catalog.CreateGamingLobby(gamingId, host, "Host");

        Assert.True(catalog.TryGetChatHub(chatId, out var foundChat));
        Assert.Same(chat, foundChat);
        Assert.True(catalog.TryGetHostedChatHub(hostedId, out var foundHosted));
        Assert.Same(hosted, foundHosted);
        Assert.True(catalog.TryGetGamingLobby(gamingId, out var foundGaming));
        Assert.Same(gaming, foundGaming);
        Assert.True(catalog.RemoveChatHub(chatId, chat));
        Assert.False(catalog.TryGetChatHub(chatId, out _));
    }

    [Fact]
    public void DependencyInjection_Registers_One_Catalog()
    {
        var services = new ServiceCollection();
        services.AddP2PHubs();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IHubCatalog>(), provider.GetRequiredService<IHubCatalog>());
    }

    [Fact]
    public void DependencyInjection_Preserves_A_Custom_TimeProvider()
    {
        var custom = new TestTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(custom);
        services.AddP2PHubs();
        using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<TimeProvider>());
    }

    private sealed class TestTimeProvider : TimeProvider;
}