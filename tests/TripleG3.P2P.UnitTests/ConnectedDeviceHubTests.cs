using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class ConnectedDeviceHubTests
{
    [Fact]
    public void Connect_Query_And_Broadcast_Use_Current_Connections()
    {
        var hub = CreateHub();
        DeviceConnection first = Connection();
        DeviceConnection second = Connection();
        hub.Connect(first, "Phone", "phone-route");
        hub.Connect(second, "Desktop", "desktop-route");

        ConnectedDeviceDispatch<string, string> dispatch = hub.Broadcast(first, "turn");

        Assert.Equal(2, hub.Snapshot.ConnectedDeviceCount);
        Assert.True(hub.IsConnected(second));
        ConnectedDeviceRoute<string> recipient = Assert.Single(dispatch.Recipients);
        Assert.Equal(second, new DeviceConnection(recipient.DeviceId, recipient.ConnectionId));
        Assert.Equal("desktop-route", recipient.Route);
        Assert.Equal(2, hub.Snapshot.MembershipRevision);
    }

    [Fact]
    public void Reconnect_Revokes_Old_Route_And_Stale_Disconnect_Is_Ignored()
    {
        var hub = CreateHub();
        Guid deviceId = Guid.NewGuid();
        var oldConnection = new DeviceConnection(deviceId, Guid.NewGuid());
        var newConnection = new DeviceConnection(deviceId, Guid.NewGuid());
        DeviceConnection sender = Connection();
        hub.Connect(sender, "Sender", "sender-route");
        hub.Connect(oldConnection, "Phone", "old-route");
        ConnectedDeviceRoute<string> oldRoute = Assert.Single(hub.RouteTo(sender, deviceId, "message").Recipients);

        ConnectedDeviceHubSnapshot<string, string, string> replaced = hub.Connect(newConnection, "Phone", "new-route");
        ConnectedDeviceHubSnapshot<string, string, string> afterStaleDisconnect = hub.Disconnect(oldConnection);

        Assert.False(hub.IsRouteCurrent(oldRoute));
        Assert.True(hub.IsConnected(newConnection));
        Assert.Equal(replaced.Revision, afterStaleDisconnect.Revision);
        DeviceMembershipChange reconnect = replaced.MembershipHistory[^1];
        Assert.Equal(DeviceMembershipChangeKind.Reconnected, reconnect.Kind);
        Assert.Equal(oldConnection.ConnectionId, reconnect.PreviousConnectionId);
        Assert.Equal(newConnection.ConnectionId, reconnect.ConnectionId);
    }

    [Fact]
    public void Leave_And_Disconnect_Report_Different_Changes()
    {
        var hub = CreateHub();
        DeviceConnection graceful = Connection();
        DeviceConnection lost = Connection();
        hub.Connect(graceful, "Graceful", "route-1");
        hub.Connect(lost, "Lost", "route-2");

        hub.Leave(graceful);
        hub.Disconnect(lost);

        Assert.Equal(DeviceMembershipChangeKind.Left, hub.Snapshot.MembershipHistory[^2].Kind);
        Assert.Equal(DeviceMembershipChangeKind.Disconnected, hub.Snapshot.MembershipHistory[^1].Kind);
        Assert.Empty(hub.GetConnectedDevices());
    }

    [Fact]
    public void Live_Session_Follows_Offer_Accept_Start_Stop_Lifecycle()
    {
        var hub = CreateHub();
        DeviceConnection origin = Connection();
        DeviceConnection remote = Connection();
        Guid sessionId = Guid.NewGuid();
        var stream = new LiveStreamDescriptor<string>(Guid.NewGuid(), "video", LiveStreamDirection.Send, "h264");
        hub.Connect(origin, "Origin", "origin-route");
        hub.Connect(remote, "Remote", "remote-route");
        long membershipRevision = hub.Snapshot.MembershipRevision;

        hub.OfferSession(origin, remote.DeviceId, sessionId, [stream]);
        hub.AnswerSession(remote, sessionId, LiveSessionAnswer.Accept, [stream]);
        hub.StartSession(origin, sessionId);
        hub.ActivateSession(remote, sessionId);
        hub.StopSession(remote, sessionId, "Finishing");
        ConnectedDeviceDispatch<LiveSessionControl<string>, string> stopped = hub.CompleteStopSession(origin, sessionId, "Finished");

        Assert.Equal(LiveSessionState.Stopped, stopped.Message.Session.State);
        Assert.Equal("Finished", stopped.Message.Session.Detail);
        Assert.Equal(membershipRevision, hub.Snapshot.MembershipRevision);
        Assert.Equal(remote.DeviceId, Assert.Single(stopped.Recipients).DeviceId);
    }

    [Fact]
    public void Disconnect_Fails_Associated_Active_Session()
    {
        var hub = CreateHub();
        DeviceConnection origin = Connection();
        DeviceConnection remote = Connection();
        Guid sessionId = Guid.NewGuid();
        var stream = new LiveStreamDescriptor<string>(Guid.NewGuid(), "audio", LiveStreamDirection.Bidirectional, "opus");
        hub.Connect(origin, "Origin", "origin-route");
        hub.Connect(remote, "Remote", "remote-route");
        hub.OfferSession(origin, remote.DeviceId, sessionId, [stream]);
        hub.AnswerSession(remote, sessionId, LiveSessionAnswer.Accept, [stream]);
        hub.StartSession(origin, sessionId);
        hub.ActivateSession(remote, sessionId);

        hub.Disconnect(remote);

        LiveSessionSnapshot<string> session = Assert.Single(hub.Snapshot.Sessions);
        Assert.Equal(LiveSessionState.Failed, session.State);
        Assert.Contains("disconnected", session.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejecting_Offer_Does_Not_Require_Negotiated_Streams()
    {
        using var hub = CreateHub();
        DeviceConnection origin = Connection();
        DeviceConnection remote = Connection();
        Guid sessionId = Guid.NewGuid();
        var stream = new LiveStreamDescriptor<string>(Guid.NewGuid(), "screen", LiveStreamDirection.Send, "h264");
        hub.Connect(origin, "Origin", "origin-route");
        hub.Connect(remote, "Remote", "remote-route");
        hub.OfferSession(origin, remote.DeviceId, sessionId, [stream]);

        ConnectedDeviceDispatch<LiveSessionControl<string>, string> answer =
            hub.AnswerSession(remote, sessionId, LiveSessionAnswer.Reject, []);

        Assert.Equal(LiveSessionState.Rejected, answer.Message.Session.State);
        Assert.Empty(answer.Message.Session.NegotiatedStreams);
    }

    [Fact]
    public void Accepting_Unoffered_Stream_Is_Rejected()
    {
        using var hub = CreateHub();
        DeviceConnection origin = Connection();
        DeviceConnection remote = Connection();
        Guid sessionId = Guid.NewGuid();
        var offered = new LiveStreamDescriptor<string>(Guid.NewGuid(), "screen", LiveStreamDirection.Send, "h264");
        var expanded = new LiveStreamDescriptor<string>(Guid.NewGuid(), "input", LiveStreamDirection.Receive, "mouse");
        hub.Connect(origin, "Origin", "origin-route");
        hub.Connect(remote, "Remote", "remote-route");
        hub.OfferSession(origin, remote.DeviceId, sessionId, [offered]);

        Assert.Throws<InvalidOperationException>(() =>
            hub.AnswerSession(remote, sessionId, LiveSessionAnswer.Accept, [expanded]));
    }

    [Fact]
    public void Disconnect_Publishes_Failure_Dispatch_For_Surviving_Participant()
    {
        using var hub = CreateHub();
        DeviceConnection origin = Connection();
        DeviceConnection remote = Connection();
        Guid sessionId = Guid.NewGuid();
        var stream = new LiveStreamDescriptor<string>(Guid.NewGuid(), "audio", LiveStreamDirection.Bidirectional, "opus");
        IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<string>, string>> dispatches = [];
        hub.StateChanged += (_, args) => dispatches = args.SessionDispatches;
        hub.Connect(origin, "Origin", "origin-route");
        hub.Connect(remote, "Remote", "remote-route");
        hub.OfferSession(origin, remote.DeviceId, sessionId, [stream]);

        hub.Disconnect(remote);

        ConnectedDeviceDispatch<LiveSessionControl<string>, string> dispatch = Assert.Single(dispatches);
        Assert.Equal(LiveSessionControlKind.Fail, dispatch.Message.Kind);
        Assert.Equal(origin.DeviceId, Assert.Single(dispatch.Recipients).DeviceId);
    }

    [Fact]
    public void Route_Revocation_Callback_Cannot_Block_Reconnect_Commit()
    {
        using var hub = CreateHub();
        DeviceConnection sender = Connection();
        Guid deviceId = Guid.NewGuid();
        DeviceConnection oldConnection = new(deviceId, Guid.NewGuid());
        DeviceConnection newConnection = new(deviceId, Guid.NewGuid());
        hub.Connect(sender, "Sender", "sender-route");
        hub.Connect(oldConnection, "Remote", "old-route");
        ConnectedDeviceRoute<string> route = Assert.Single(hub.RouteTo(sender, deviceId, "message").Recipients);
        using CancellationTokenRegistration registration = route.RevocationToken.Register(() => throw new InvalidOperationException("subscriber failure"));

        hub.Connect(newConnection, "Remote", "new-route");

        Assert.True(hub.IsConnected(newConnection));
        Assert.False(hub.IsRouteCurrent(route));
    }

    [Fact]
    public void Dispose_Revokes_Routes_And_Rejects_Further_Use()
    {
        var hub = CreateHub();
        DeviceConnection sender = Connection();
        DeviceConnection remote = Connection();
        hub.Connect(sender, "Sender", "sender-route");
        hub.Connect(remote, "Remote", "remote-route");
        ConnectedDeviceRoute<string> route = Assert.Single(hub.RouteTo(sender, remote.DeviceId, "message").Recipients);

        hub.Dispose();

        Assert.True(route.RevocationToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => _ = hub.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => hub.Connect(Connection(), "New", "new-route"));
    }

    [Fact]
    public void State_Events_Are_Published_In_Revision_Order()
    {
        var hub = CreateHub();
        var revisions = new List<long>();
        hub.StateChanged += (_, args) => revisions.Add(args.Snapshot.Revision);

        hub.Connect(Connection(), "One", "route-1");
        hub.Connect(Connection(), "Two", "route-2");
        hub.Connect(Connection(), "Three", "route-3");

        Assert.Equal([1L, 2L, 3L], revisions);
    }

    private static ConnectedDeviceHub<string, string, string> CreateHub()
        => new(Guid.NewGuid());

    private static DeviceConnection Connection()
        => new(Guid.NewGuid(), Guid.NewGuid());
}
