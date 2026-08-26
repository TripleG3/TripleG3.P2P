namespace TripleG3.P2P.Hubs;

public sealed record NotificationsHubSnapshot(
    Guid HubId,
    long Revision,
    IReadOnlyList<NotificationDevice> Devices)
{
    public int DeviceCount => Devices.Count;

    public int UserCount => Devices.Select(device => device.UserId).Distinct().Count();
}