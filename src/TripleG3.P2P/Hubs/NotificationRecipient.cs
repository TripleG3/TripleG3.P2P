namespace TripleG3.P2P.Hubs;

public sealed record NotificationRecipient(
    NotificationRecipientKind Kind,
    IReadOnlyList<Guid> UserIds,
    IReadOnlyList<Guid> DeviceIds,
    IReadOnlyList<NotificationPlatform> Platforms)
{
    public static NotificationRecipient AllDevices() => new(
        NotificationRecipientKind.AllDevices,
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(Array.Empty<NotificationPlatform>()));

    public static NotificationRecipient ForUsers(params Guid[] userIds) => new(
        NotificationRecipientKind.Users,
        Array.AsReadOnly(userIds?.ToArray() ?? throw new ArgumentNullException(nameof(userIds))),
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(Array.Empty<NotificationPlatform>()));

    public static NotificationRecipient ForDevices(params Guid[] deviceIds) => new(
        NotificationRecipientKind.Devices,
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(deviceIds?.ToArray() ?? throw new ArgumentNullException(nameof(deviceIds))),
        Array.AsReadOnly(Array.Empty<NotificationPlatform>()));

    public static NotificationRecipient ForPlatforms(params NotificationPlatform[] platforms) => new(
        NotificationRecipientKind.Platforms,
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(Array.Empty<Guid>()),
        Array.AsReadOnly(platforms?.ToArray() ?? throw new ArgumentNullException(nameof(platforms))));
}