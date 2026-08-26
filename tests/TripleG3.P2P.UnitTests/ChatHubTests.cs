using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class ChatHubTests
{
    [Fact]
    public void Ownerless_Hub_Tracks_Members_Messages_And_Notifications()
    {
        var hub = new ChatHub(Guid.NewGuid());
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        hub.Join(alice, "Alice");
        hub.Join(bob, "Bob");
        var dispatch = hub.SendMessage(alice, "Hello");
        var snapshot = hub.Leave(bob);

        Assert.Equal(1, snapshot.MemberCount);
        Assert.Equal("Hello", Assert.Single(snapshot.Messages).Text);
        Assert.Equal([bob], dispatch.RecipientMemberIds);
        Assert.Equal(3, snapshot.Notifications.Count);
        Assert.Equal(HubNotificationKind.MemberLeft, snapshot.Notifications[^1].Kind);
        Assert.Equal(4, snapshot.Revision);
    }

    [Fact]
    public void Ownerless_Hub_Rejects_Duplicate_Usernames_And_Bounds_History()
    {
        var hub = new ChatHub(
            Guid.NewGuid(),
            new HubOptions { MessageHistoryCapacity = 2, NotificationHistoryCapacity = 1 });
        var member = Guid.NewGuid();
        hub.Join(member, "Alice");

        Assert.Throws<InvalidOperationException>(() => hub.Join(Guid.NewGuid(), "alice"));
        hub.SendMessage(member, "one");
        hub.SendMessage(member, "two");
        hub.SendMessage(member, "three");

        Assert.Equal(["two", "three"], hub.Snapshot.Messages.Select(message => message.Text));
        Assert.Single(hub.Snapshot.Notifications);
    }

    [Fact]
    public void State_Handlers_Are_Isolated_And_Can_Reenter()
    {
        var hub = new ChatHub(Guid.NewGuid());
        var observedRevision = 0L;
        hub.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
        hub.StateChanged += (_, _) => observedRevision = hub.Snapshot.Revision;

        var snapshot = hub.Join(Guid.NewGuid(), "Alice");

        Assert.Equal(snapshot.Revision, observedRevision);
    }

    [Fact]
    public async Task Concurrent_Joins_Do_Not_Lose_Members()
    {
        var hub = new ChatHub(Guid.NewGuid(), new HubOptions { MaximumMembers = 100 });
        var members = Enumerable.Range(0, 50).Select(index => (Id: Guid.NewGuid(), Username: $"Member{index}")).ToArray();

        await Task.WhenAll(members.Select(member => Task.Run(() => hub.Join(member.Id, member.Username))));

        Assert.Equal(50, hub.Snapshot.MemberCount);
        Assert.Equal(50, hub.Snapshot.Revision);
    }
}