using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.UnitTests;

public sealed class NotificationsHubTests
{
    [Fact]
    public void User_Targeting_Routes_To_Every_Registered_Device_For_That_User()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var windows = hub.RegisterDevice(Guid.NewGuid(), user, NotificationPlatform.Windows, "en-US");
        var android = hub.RegisterDevice(Guid.NewGuid(), user, NotificationPlatform.Android, "en-US");
        _ = hub.RegisterDevice(Guid.NewGuid(), otherUser, NotificationPlatform.Ios, "en-US");

        var dispatch = hub.Route(CreateRequest(), NotificationRecipient.ForUsers(user));

        Assert.Equal(2, dispatch.Deliveries.Count);
        Assert.Equal(new[] { windows.DeviceId, android.DeviceId }.Order(), dispatch.Deliveries.Select(delivery => delivery.DeviceId).Order());
        Assert.All(dispatch.Deliveries, delivery => Assert.Same(dispatch.Notification, delivery.Notification));
    }

    [Fact]
    public void Platform_Targeting_Produces_Windows_Android_And_Ios_Views()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        var windows = hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Windows, "en-US");
        var android = hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Android, "en-US");
        var ios = hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Ios, "en-US");
        var request = CreateRequest() with
        {
            ImageUri = "https://example.test/image.png",
            Category = "messages",
            ThreadId = "thread-1",
            Tag = "message-1",
            Badge = 3,
            Data =
            [
                new NotificationDataEntry("launchUri", "app://messages/1"),
                new NotificationDataEntry("androidChannelId", "chat"),
                new NotificationDataEntry("androidSmallIcon", "ic_chat")
            ]
        };

        var dispatch = hub.Route(request, NotificationRecipient.AllDevices());

        var windowsDelivery = Assert.Single(dispatch.Deliveries, delivery => delivery.DeviceId == windows.DeviceId);
        Assert.Equal("app://messages/1", windowsDelivery.PlatformView.Windows!.LaunchUri);
        var androidDelivery = Assert.Single(dispatch.Deliveries, delivery => delivery.DeviceId == android.DeviceId);
        Assert.Equal("chat", androidDelivery.PlatformView.Android!.ChannelId);
        Assert.Equal("ic_chat", androidDelivery.PlatformView.Android.SmallIcon);
        var iosDelivery = Assert.Single(dispatch.Deliveries, delivery => delivery.DeviceId == ios.DeviceId);
        Assert.Equal(3, iosDelivery.PlatformView.Ios!.Badge);
        Assert.True(iosDelivery.PlatformView.Ios.IsMutableContent);

        var wire = androidDelivery.ToWireDelivery();
        var androidView = wire.ReadPlatformView<AndroidNotificationView>();
        Assert.Equal("chat", androidView.ChannelId);
        Assert.Equal("ic_chat", androidView.SmallIcon);
    }

    [Fact]
    public void Device_Registration_Can_Be_Updated_And_Removed()
    {
        var deviceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = new NotificationsHub(Guid.NewGuid());
        hub.RegisterDevice(deviceId, userId, NotificationPlatform.Windows, "en-US");

        var updated = hub.RegisterDevice(deviceId, userId, NotificationPlatform.Android, "fr-FR", "Europe/Paris");

        Assert.Single(hub.Snapshot.Devices);
        Assert.Equal(NotificationPlatform.Android, updated.Platform);
        Assert.Equal("fr-FR", updated.Locale);
        Assert.True(hub.UnregisterDevice(deviceId));
        Assert.False(hub.UnregisterDevice(deviceId));
        Assert.Empty(hub.Snapshot.Devices);
    }

    [Fact]
    public void No_Matching_Recipients_Returns_An_Empty_Dispatch()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Windows, "en-US");

        var dispatch = hub.Route(CreateRequest(), NotificationRecipient.ForUsers(Guid.NewGuid()));

        Assert.Empty(dispatch.Deliveries);
        Assert.Equal(hub.Snapshot.Revision, dispatch.Revision);
    }

    [Fact]
    public void Expired_Notifications_Are_Rejected()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        var request = CreateRequest() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        Assert.Throws<InvalidOperationException>(() => hub.Route(request, NotificationRecipient.AllDevices()));
    }

    [Fact]
    public void Route_Defensively_Snapshots_Data_And_Actions()
    {
        var data = new List<NotificationDataEntry> { new("key", "original") };
        var actions = new List<NotificationAction> { new("open", "Open", null) };
        var hub = new NotificationsHub(Guid.NewGuid());
        hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Windows, "en-US");
        var request = CreateRequest() with { Data = data, Actions = actions };

        var dispatch = hub.Route(request, NotificationRecipient.AllDevices());
        data[0] = new NotificationDataEntry("key", "changed");
        actions[0] = new NotificationAction("other", "Other", null);

        Assert.Equal("original", dispatch.Notification.Data[0].Value);
        Assert.Equal("open", dispatch.Notification.Actions[0].ActionId);
    }

    [Fact]
    public void Route_Rejects_Duplicate_Actions_And_Contradictory_Selectors()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        var duplicateActions = CreateRequest() with
        {
            Actions = [new NotificationAction("open", "Open", null), new NotificationAction("OPEN", "Open again", null)]
        };
        var contradictory = new NotificationRecipient(
            NotificationRecipientKind.Users,
            [Guid.NewGuid()],
            [Guid.NewGuid()],
            []);

        Assert.Throws<ArgumentException>(() => hub.Route(duplicateActions, NotificationRecipient.AllDevices()));
        Assert.Throws<ArgumentException>(() => hub.Route(CreateRequest(), contradictory));
    }

    [Fact]
    public void Silent_Projection_Removes_Sound_And_Wire_View_Requires_The_Matching_Platform_Type()
    {
        var hub = new NotificationsHub(Guid.NewGuid());
        var device = hub.RegisterDevice(Guid.NewGuid(), Guid.NewGuid(), NotificationPlatform.Android, "en-US");
        var dispatch = hub.Route(CreateRequest() with { IsSilent = true }, NotificationRecipient.ForDevices(device.DeviceId));
        var delivery = Assert.Single(dispatch.Deliveries);
        var wire = delivery.ToWireDelivery();

        Assert.Null(delivery.PlatformView.Android!.Sound);
        Assert.Throws<InvalidOperationException>(() => wire.ReadPlatformView<IosNotificationView>());
    }

    private static NotificationRequest CreateRequest()
        => new(
            "Match ready",
            "Your match is ready.",
            Subtitle: "Lobby",
            Sound: "default",
            Priority: NotificationPriority.High,
            Actions: [new NotificationAction("open", "Open", "app://match")]);
}