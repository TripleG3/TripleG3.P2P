using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using TripleG3.P2P.Attributes;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class ChatHubIntegrationTests
{
    public static TheoryData<string> Transports => new()
    {
        "udp",
        "tcp"
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Ownerless_Chat_Delivers_Over_Transport_And_Stops_Routing_To_Members_Who_Left(string transport)
    {
        var createBus = GetBusFactory(transport);
        await using var harness = new HubTransportTestHarness(createBus);
        var hub = new ChatHub(Guid.NewGuid());
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var aliceSession = await harness.AddMemberAsync(alice);
        var bobSession = await harness.AddMemberAsync(bob);
        var carolSession = await harness.AddMemberAsync(carol);
        hub.Join(alice, "Alice");
        hub.Join(bob, "Bob");
        hub.Join(carol, "Carol");

        await harness.PublishAsync(hub.SendMessage(alice, "first"));
        await bobSession.WaitForMessageCountAsync(1);
        await carolSession.WaitForMessageCountAsync(1);
        Assert.Empty(aliceSession.Messages);

        hub.Leave(carol);
        await harness.PublishAsync(hub.SendMessage(alice, "second"));
        await bobSession.WaitForMessageCountAsync(2);
        await Task.Delay(150);

        Assert.Single(carolSession.Messages);
        Assert.Equal(["first", "second"], bobSession.Messages.Select(message => message.Text));
        Assert.All(bobSession.Messages, message => Assert.Equal(hub.Snapshot.HubId, message.HubId));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Hosted_Chat_Moderation_Changes_Real_Transport_Recipients(string transport)
    {
        var createBus = GetBusFactory(transport);
        await using var harness = new HubTransportTestHarness(createBus);
        var host = Guid.NewGuid();
        var firstMember = Guid.NewGuid();
        var removedMember = Guid.NewGuid();
        var hub = new HostedChatHub(Guid.NewGuid(), host, "Host");
        var hostSession = await harness.AddMemberAsync(host);
        var firstSession = await harness.AddMemberAsync(firstMember);
        var removedSession = await harness.AddMemberAsync(removedMember);
        hub.AddMember(host, firstMember, "First");
        hub.AddMember(host, removedMember, "Removed");
        hub.PromoteMember(host, firstMember);
        hub.RemoveMember(firstMember, removedMember);

        await harness.PublishAsync(hub.SendMessage(host, "hosts only plus members"));
        await firstSession.WaitForMessageCountAsync(1);
        await Task.Delay(150);

        Assert.Empty(hostSession.Messages);
        Assert.Empty(removedSession.Messages);
        Assert.Equal(firstMember, Assert.Single(hub.Snapshot.Members, member => member.Role == HubMemberRole.Host && member.MemberId != host).MemberId);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Ownerless_Chat_Routes_A_Custom_Message_Type_Over_Transport(string transport)
    {
        await using var harness = new HubTransportTestHarness(GetBusFactory(transport));
        var hub = new ChatHub(Guid.NewGuid());
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var senderSession = await harness.AddMemberAsync(sender);
        var recipientSession = await harness.AddMemberAsync(recipient);
        var senderMessages = senderSession.Subscribe<TypingIndicator>();
        var recipientMessages = recipientSession.Subscribe<TypingIndicator>();
        hub.Join(sender, "Sender");
        hub.Join(recipient, "Recipient");

        var dispatch = hub.RouteMessage(sender, new TypingIndicator(true));
        await harness.PublishAsync(dispatch);
        await WaitForAsync(() => recipientMessages.Count == 1);

        Assert.Empty(senderMessages);
        Assert.True(Assert.Single(recipientMessages).IsTyping);
        Assert.Empty(hub.Snapshot.Messages);
        Assert.Equal(hub.Snapshot.Revision, dispatch.Revision);
    }

    private static Func<ISerialBus> GetBusFactory(string transport)
        => transport switch
        {
            "udp" => SerialBusFactory.CreateUdp,
            "tcp" => SerialBusFactory.CreateTcp,
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Expected custom hub message was not delivered before timeout.");
    }

    [P2PMessage("TypingIndicator")]
    public sealed record TypingIndicator([property: P2PProperty(1)] bool IsTyping);
}