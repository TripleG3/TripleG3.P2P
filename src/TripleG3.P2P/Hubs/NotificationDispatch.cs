namespace TripleG3.P2P.Hubs;

public sealed record NotificationDispatch(
    Guid HubId,
    long Revision,
    NotificationMessage Notification,
    IReadOnlyList<NotificationDelivery> Deliveries,
    DateTimeOffset CreatedAt);