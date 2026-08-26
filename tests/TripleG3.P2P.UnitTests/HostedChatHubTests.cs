using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class HostedChatHubTests
{
    [Fact]
    public void Host_Can_Add_Promote_Demote_And_Remove_Members()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var hub = new HostedChatHub(Guid.NewGuid(), host, "Host");

        hub.AddMember(host, member, "Player");
        hub.PromoteMember(host, member);
        Assert.Equal(HubMemberRole.Host, hub.Snapshot.Members.Single(candidate => candidate.MemberId == member).Role);
        hub.DemoteMember(host, member);
        var snapshot = hub.RemoveMember(host, member);

        Assert.Single(snapshot.Members);
        Assert.DoesNotContain(snapshot.Members, candidate => candidate.MemberId == member);
    }

    [Fact]
    public void Member_Cannot_Moderate_And_Final_Host_Cannot_Leave_Others()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var hub = new HostedChatHub(Guid.NewGuid(), host, "Host");
        hub.AddMember(host, member, "Player");

        Assert.Throws<UnauthorizedAccessException>(() => hub.AddMember(member, Guid.NewGuid(), "Other"));
        Assert.Throws<InvalidOperationException>(() => hub.Leave(host));
        Assert.Equal(2, hub.Snapshot.MemberCount);
    }

    [Fact]
    public void Promoted_Host_Allows_Original_Host_To_Leave()
    {
        var host = Guid.NewGuid();
        var member = Guid.NewGuid();
        var hub = new HostedChatHub(Guid.NewGuid(), host, "Host");
        hub.AddMember(host, member, "Player");
        hub.PromoteMember(host, member);

        var snapshot = hub.Leave(host);

        var remaining = Assert.Single(snapshot.Members);
        Assert.Equal(member, remaining.MemberId);
        Assert.Equal(HubMemberRole.Host, remaining.Role);
    }

    [Fact]
    public void Final_Host_Cannot_Leave_An_Empty_Hosted_Hub()
    {
        var host = Guid.NewGuid();
        var hub = new HostedChatHub(Guid.NewGuid(), host, "Host");

        Assert.Throws<InvalidOperationException>(() => hub.Leave(host));
    }
}