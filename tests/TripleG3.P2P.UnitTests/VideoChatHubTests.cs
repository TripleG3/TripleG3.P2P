using TripleG3.P2P.Attributes;
using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class VideoChatHubTests
{
    [Fact]
    public void Members_Default_To_No_Media_And_Camera_Microphone_Are_Independent()
    {
        var member = Guid.NewGuid();
        var hub = new VideoChatHub(Guid.NewGuid());

        hub.Join(member, "Alice");
        hub.SetCameraEnabled(member, true);
        var cameraOnly = Assert.Single(hub.Snapshot.Members);
        Assert.True(cameraOnly.IsCameraEnabled);
        Assert.False(cameraOnly.IsMicrophoneEnabled);
        hub.SetMicrophoneEnabled(member, true);
        hub.SetCameraEnabled(member, false);
        var microphoneOnly = Assert.Single(hub.Snapshot.Members);

        Assert.False(microphoneOnly.IsCameraEnabled);
        Assert.True(microphoneOnly.IsMicrophoneEnabled);
    }

    [Fact]
    public void Media_Routes_Require_Enabled_Sources_And_Contain_All_Other_Members()
    {
        var sender = Guid.NewGuid();
        var firstRecipient = Guid.NewGuid();
        var secondRecipient = Guid.NewGuid();
        var hub = BuildHub(sender, firstRecipient, secondRecipient);

        Assert.Throws<InvalidOperationException>(() => hub.GetMediaRoute(sender, VideoChatMediaKind.Audio));
        hub.SetMicrophoneEnabled(sender, true);
        hub.SetCameraEnabled(sender, true);
        var route = hub.GetMediaRoute(sender, VideoChatMediaKind.AudioAndVideo);

        Assert.Equal(new[] { firstRecipient, secondRecipient }.Order(), route.RecipientMemberIds.Order());
        Assert.True(hub.IsRouteCurrent(route));
    }

    [Fact]
    public void Routing_Revision_Changes_For_Membership_And_Media_But_Not_Chat()
    {
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var hub = BuildHub(sender, recipient);
        hub.SetMicrophoneEnabled(sender, true);
        var route = hub.GetMediaRoute(sender, VideoChatMediaKind.Audio);
        var routingRevision = hub.Snapshot.RoutingRevision;

        hub.SendMessage(sender, "hello");
        hub.RouteMessage(sender, new HandRaised(true));

        Assert.Equal(routingRevision, hub.Snapshot.RoutingRevision);
        Assert.True(hub.IsRouteCurrent(route));
        hub.SetCameraEnabled(recipient, true);
        Assert.True(route.RevocationToken.IsCancellationRequested);
        Assert.False(hub.IsRouteCurrent(route));
    }

    [Fact]
    public void Leaving_And_Disabling_Media_Invalidates_Routes()
    {
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var hub = BuildHub(sender, recipient);
        hub.SetCameraEnabled(sender, true);
        var route = hub.GetMediaRoute(sender, VideoChatMediaKind.Video);

        hub.SetCameraEnabled(sender, false);
        Assert.False(hub.IsRouteCurrent(route));
        hub.SetCameraEnabled(sender, true);
        route = hub.GetMediaRoute(sender, VideoChatMediaKind.Video);
        hub.Leave(recipient);

        Assert.False(hub.IsRouteCurrent(route));
        Assert.Empty(hub.GetMediaRoute(sender, VideoChatMediaKind.Video).RecipientMemberIds);
    }

    [Fact]
    public void Text_Is_Retained_And_Custom_Messages_Are_Not()
    {
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var hub = BuildHub(sender, recipient);

        var text = hub.SendMessage(sender, "hello");
        var custom = hub.RouteMessage(sender, new HandRaised(true));

        Assert.Single(hub.Snapshot.Messages);
        Assert.Equal("hello", hub.Snapshot.Messages[0].Text);
        Assert.Equal([recipient], text.RecipientMemberIds);
        Assert.Equal([recipient], custom.RecipientMemberIds);
    }

    [Theory]
    [InlineData(1_000_000L, 1_000_000L, 48_000u, 90_000u)]
    [InlineData(10_000_000L, 10_000_000L, 48_000u, 90_000u)]
    [InlineData(3_000_000L, 1_500_000L, 24_000u, 45_000u)]
    public void Media_Clock_Maps_One_Capture_Timeline_To_Audio_And_Video(
        long frequency,
        long delta,
        uint expectedAudio,
        uint expectedVideo)
    {
        var clock = new RtpMediaClock(100, frequency, 0, 0);

        var timestamps = clock.Map(100 + delta);

        Assert.Equal(expectedAudio, timestamps.AudioTimestamp48k);
        Assert.Equal(expectedVideo, timestamps.VideoTimestamp90k);
    }

    [Fact]
    public void Media_Clock_Supports_Rtp_Wrap_With_Overflow_Safe_Math()
    {
        var clock = new RtpMediaClock(0, 1, uint.MaxValue - 10, uint.MaxValue - 10);

        var timestamps = clock.Map(1);

        Assert.Equal(unchecked((uint)(uint.MaxValue - 10 + 48_000)), timestamps.AudioTimestamp48k);
        Assert.Equal(unchecked((uint)(uint.MaxValue - 10 + 90_000)), timestamps.VideoTimestamp90k);
    }

    private static VideoChatHub BuildHub(params Guid[] members)
    {
        var hub = new VideoChatHub(Guid.NewGuid());
        for (var index = 0; index < members.Length; index++)
        {
            hub.Join(members[index], $"Member{index}");
        }
        return hub;
    }

    [P2PMessage("HandRaised")]
    public sealed record HandRaised([property: P2PProperty(1)] bool IsRaised);
}