using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class GamingLobbyHubTests
{
    [Fact]
    public void Teams_Support_Assignment_Reassignment_And_Unassignment()
    {
        var host = Guid.NewGuid();
        var player = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var hub = new GamingLobbyHub(Guid.NewGuid(), host, "Host");
        hub.AddMember(host, player, "Player");
        hub.AddTeam(host, red, "Red");
        hub.AddTeam(host, blue, "Blue");

        hub.AssignMemberToTeam(host, player, red);
        Assert.Equal(1, hub.Snapshot.Teams.Single(team => team.TeamId == red).MemberCount);
        hub.AssignMemberToTeam(host, player, blue);
        Assert.Equal(0, hub.Snapshot.Teams.Single(team => team.TeamId == red).MemberCount);
        Assert.Equal(1, hub.Snapshot.Teams.Single(team => team.TeamId == blue).MemberCount);
        hub.UnassignMemberFromTeam(host, player);

        Assert.Equal(Guid.Empty, hub.Snapshot.Members.Single(member => member.MemberId == player).TeamId);
        hub.RemoveTeam(host, blue);
        Assert.DoesNotContain(hub.Snapshot.Teams, team => team.TeamId == blue);
    }

    [Fact]
    public void Nonempty_Team_Cannot_Be_Removed()
    {
        var host = Guid.NewGuid();
        var team = Guid.NewGuid();
        var hub = new GamingLobbyHub(Guid.NewGuid(), host, "Host");
        hub.AddTeam(host, team, "Red");
        hub.AssignMemberToTeam(host, host, team);

        Assert.Throws<InvalidOperationException>(() => hub.RemoveTeam(host, team));
    }

    [Fact]
    public void Chat_Routes_To_All_Or_Only_The_Senders_Team()
    {
        var host = Guid.NewGuid();
        var redPlayer = Guid.NewGuid();
        var bluePlayer = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var hub = BuildPopulatedLobby(host, redPlayer, bluePlayer, red, blue);

        var all = hub.SendChat(host, HubAudience.All, Guid.Empty, "everyone");
        var team = hub.SendChat(redPlayer, HubAudience.Team, red, "red only");

        Assert.Equal(new[] { redPlayer, bluePlayer }.Order(), all.RecipientMemberIds.Order());
        Assert.Equal([host], team.RecipientMemberIds);
        Assert.DoesNotContain(hub.Snapshot.Messages, message => message.Audience == HubAudience.Team);
        Assert.Contains(hub.GetMessagesForMember(redPlayer), message => message.Text == "red only");
        Assert.DoesNotContain(hub.GetMessagesForMember(bluePlayer), message => message.Text == "red only");
        Assert.Throws<UnauthorizedAccessException>(() => hub.SendChat(redPlayer, HubAudience.Team, blue, "invalid"));
    }

    [Fact]
    public void Audio_Policy_Controls_All_And_Team_Routes()
    {
        var host = Guid.NewGuid();
        var redPlayer = Guid.NewGuid();
        var bluePlayer = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var hub = BuildPopulatedLobby(host, redPlayer, bluePlayer, red, blue);

        hub.SetAudioPolicy(host, GamingLobbyAudioPolicy.Team);
        var team = hub.GetAudioRoute(redPlayer, HubAudience.Team, red);

        Assert.Equal([host], team.RecipientMemberIds);
        Assert.True(hub.IsAudioRouteCurrent(team));
        hub.UnassignMemberFromTeam(host, redPlayer);
        Assert.False(hub.IsAudioRouteCurrent(team));
        Assert.Throws<InvalidOperationException>(() => hub.GetAudioRoute(host, HubAudience.All, Guid.Empty));
        hub.SetAudioPolicy(host, GamingLobbyAudioPolicy.None);
        Assert.Throws<InvalidOperationException>(() => hub.GetAudioRoute(redPlayer, HubAudience.Team, red));
    }

    private static GamingLobbyHub BuildPopulatedLobby(Guid host, Guid redPlayer, Guid bluePlayer, Guid red, Guid blue)
    {
        var hub = new GamingLobbyHub(Guid.NewGuid(), host, "Host");
        hub.AddMember(host, redPlayer, "RedPlayer");
        hub.AddMember(host, bluePlayer, "BluePlayer");
        hub.AddTeam(host, red, "Red");
        hub.AddTeam(host, blue, "Blue");
        hub.AssignMemberToTeam(host, host, red);
        hub.AssignMemberToTeam(host, redPlayer, red);
        hub.AssignMemberToTeam(host, bluePlayer, blue);
        return hub;
    }
}