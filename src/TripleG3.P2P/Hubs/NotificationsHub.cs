using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>
/// Routes platform-neutral notifications to registered device profiles and produces local platform views.
/// This type does not integrate with an external push notification provider.
/// </summary>
public sealed class NotificationsHub : INotificationsHub
{
    private readonly object _gate = new();
    private readonly NotificationsHubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly INotificationProjector _projector;
    private readonly ILogger<NotificationsHub> _logger;
    private NotificationsHubSnapshot _snapshot;

    public NotificationsHub(
        Guid hubId,
        NotificationsHubOptions? options = null,
        TimeProvider? timeProvider = null,
        INotificationProjector? projector = null,
        ILogger<NotificationsHub>? logger = null)
    {
        HubValidation.ValidateId(hubId, nameof(hubId));
        _options = options ?? new NotificationsHubOptions();
        ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _projector = projector ?? new DefaultNotificationProjector();
        _logger = logger ?? NullLogger<NotificationsHub>.Instance;
        _snapshot = new NotificationsHubSnapshot(hubId, 0, HubValidation.Snapshot<NotificationDevice>([]));
    }

    public NotificationsHubSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<HubStateChangedEventArgs<NotificationsHubSnapshot>>? StateChanged;

    public NotificationDevice RegisterDevice(
        Guid deviceId,
        Guid userId,
        NotificationPlatform platform,
        string locale,
        string? timeZoneId = null)
    {
        HubValidation.ValidateId(deviceId, nameof(deviceId));
        HubValidation.ValidateId(userId, nameof(userId));
        if (!Enum.IsDefined(platform)) throw new ArgumentOutOfRangeException(nameof(platform));
        var normalizedLocale = NormalizeOptional(locale, 32, nameof(locale), required: true)!;
        var normalizedTimeZone = NormalizeOptional(timeZoneId, 128, nameof(timeZoneId), required: false);
        NotificationsHubSnapshot changed;
        NotificationDevice registered;

        lock (_gate)
        {
            var current = _snapshot;
            var existing = current.Devices.FirstOrDefault(device => device.DeviceId == deviceId);
            if (existing is null && current.Devices.Count >= _options.MaximumDevices)
            {
                throw new InvalidOperationException("The notifications hub has reached its device capacity.");
            }

            registered = new NotificationDevice(
                deviceId,
                userId,
                platform,
                normalizedLocale,
                normalizedTimeZone,
                existing?.RegisteredAt ?? _timeProvider.GetUtcNow());
            var devices = existing is null
                ? current.Devices.Append(registered)
                : current.Devices.Select(device => device.DeviceId == deviceId ? registered : device);
            changed = current with
            {
                Revision = current.Revision + 1,
                Devices = HubValidation.Snapshot(devices.OrderBy(device => device.DeviceId))
            };
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return registered;
    }

    public bool UnregisterDevice(Guid deviceId)
    {
        HubValidation.ValidateId(deviceId, nameof(deviceId));
        NotificationsHubSnapshot? changed = null;
        lock (_gate)
        {
            var current = _snapshot;
            if (!current.Devices.Any(device => device.DeviceId == deviceId)) return false;
            changed = current with
            {
                Revision = current.Revision + 1,
                Devices = HubValidation.Snapshot(current.Devices.Where(device => device.DeviceId != deviceId))
            };
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return true;
    }

    public NotificationDispatch Route(NotificationMessage notification, NotificationRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(recipient);
        ValidateMessage(notification);
        var normalizedNotification = SnapshotNotification(notification);
        var normalizedRecipient = NormalizeRecipient(recipient);
        NotificationsHubSnapshot changed;
        NotificationDispatch dispatch;

        lock (_gate)
        {
            var current = _snapshot;
            var now = _timeProvider.GetUtcNow();
            if (normalizedNotification.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                throw new InvalidOperationException("An expired notification cannot be routed.");
            }

            var nextRevision = current.Revision + 1;
            var deliveries = SelectDevices(current.Devices, normalizedRecipient)
                .Select(device => new NotificationDelivery(
                    Guid.NewGuid(),
                    current.HubId,
                    nextRevision,
                    device.DeviceId,
                    device.UserId,
                    normalizedNotification,
                    _projector.Project(normalizedNotification, device),
                    now))
                .ToArray();
            changed = current with { Revision = nextRevision };
            dispatch = new NotificationDispatch(
                current.HubId,
                nextRevision,
                normalizedNotification,
                Array.AsReadOnly(deliveries),
                now);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public NotificationDispatch Route(NotificationRequest request, NotificationRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _timeProvider.GetUtcNow();
        var notification = new NotificationMessage(
            Guid.NewGuid(),
            request.Title,
            request.Body,
            request.Subtitle,
            request.ImageUri,
            request.Sound,
            request.Category,
            request.ThreadId,
            request.Tag,
            request.Badge,
            request.Priority,
            request.IsSilent,
            now,
            request.ExpiresAt,
            Array.AsReadOnly(request.Data?.ToArray() ?? []),
            Array.AsReadOnly(request.Actions?.ToArray() ?? []));
        return Route(notification, recipient);
    }

    private static IEnumerable<NotificationDevice> SelectDevices(
        IReadOnlyList<NotificationDevice> devices,
        NotificationRecipient recipient)
        => recipient.Kind switch
        {
            NotificationRecipientKind.AllDevices => devices,
            NotificationRecipientKind.Users => devices.Where(device => recipient.UserIds.Contains(device.UserId)),
            NotificationRecipientKind.Devices => devices.Where(device => recipient.DeviceIds.Contains(device.DeviceId)),
            NotificationRecipientKind.Platforms => devices.Where(device => recipient.Platforms.Contains(device.Platform)),
            _ => throw new ArgumentOutOfRangeException(nameof(recipient))
        };

    private void ValidateMessage(NotificationMessage notification)
    {
        HubValidation.ValidateId(notification.NotificationId, nameof(notification));
        _ = HubValidation.NormalizeName(notification.Title, _options.MaximumTitleLength, nameof(notification.Title));
        _ = HubValidation.NormalizeName(notification.Body, _options.MaximumBodyLength, nameof(notification.Body));
        if (!Enum.IsDefined(notification.Priority)) throw new ArgumentOutOfRangeException(nameof(notification.Priority));
        if (notification.Badge < 0) throw new ArgumentOutOfRangeException(nameof(notification.Badge));
        if (notification.Data.Count > _options.MaximumDataEntries) throw new ArgumentOutOfRangeException(nameof(notification.Data));
        if (notification.Actions.Count > _options.MaximumActions) throw new ArgumentOutOfRangeException(nameof(notification.Actions));
        if (notification.Data.Any(entry => entry is null)) throw new ArgumentException("Notification data entries cannot be null.", nameof(notification));
        if (notification.Data.Any(entry => string.IsNullOrWhiteSpace(entry.Key))) throw new ArgumentException("Notification data keys are required.", nameof(notification));
        if (notification.Data.Any(entry => entry.Key.Length > _options.MaximumDataKeyLength || entry.Value.Length > _options.MaximumDataValueLength))
        {
            throw new ArgumentOutOfRangeException(nameof(notification.Data));
        }
        if (notification.Data.Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != notification.Data.Count)
        {
            throw new ArgumentException("Notification data keys must be unique.", nameof(notification));
        }
        if (notification.Actions.Any(action => action is null)) throw new ArgumentException("Notification actions cannot be null.", nameof(notification));
        if (notification.Actions.Any(action => string.IsNullOrWhiteSpace(action.ActionId) || string.IsNullOrWhiteSpace(action.Title)))
        {
            throw new ArgumentException("Notification action identifiers and titles are required.", nameof(notification));
        }
        if (notification.Actions.Any(action => action.ActionId.Length > _options.MaximumDataKeyLength || action.Title.Length > _options.MaximumActionTitleLength))
        {
            throw new ArgumentOutOfRangeException(nameof(notification.Actions));
        }
        if (notification.Actions.Select(action => action.ActionId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != notification.Actions.Count)
        {
            throw new ArgumentException("Notification action identifiers must be unique.", nameof(notification));
        }
    }

    private static NotificationRecipient NormalizeRecipient(NotificationRecipient recipient)
    {
        if (!Enum.IsDefined(recipient.Kind)) throw new ArgumentOutOfRangeException(nameof(recipient));
        ArgumentNullException.ThrowIfNull(recipient.UserIds);
        ArgumentNullException.ThrowIfNull(recipient.DeviceIds);
        ArgumentNullException.ThrowIfNull(recipient.Platforms);
        if (recipient.UserIds.Any(id => id == Guid.Empty)) throw new ArgumentException("Recipient user IDs must be non-empty.", nameof(recipient));
        if (recipient.DeviceIds.Any(id => id == Guid.Empty)) throw new ArgumentException("Recipient device IDs must be non-empty.", nameof(recipient));
        if (recipient.Platforms.Any(platform => !Enum.IsDefined(platform))) throw new ArgumentException("Recipient platforms are invalid.", nameof(recipient));
        var userIds = Array.AsReadOnly(recipient.UserIds.Distinct().ToArray());
        var deviceIds = Array.AsReadOnly(recipient.DeviceIds.Distinct().ToArray());
        var platforms = Array.AsReadOnly(recipient.Platforms.Distinct().ToArray());
        return recipient.Kind switch
        {
            NotificationRecipientKind.AllDevices when userIds.Count == 0 && deviceIds.Count == 0 && platforms.Count == 0
                => NotificationRecipient.AllDevices(),
            NotificationRecipientKind.Users when deviceIds.Count == 0 && platforms.Count == 0
                => new NotificationRecipient(recipient.Kind, userIds, deviceIds, platforms),
            NotificationRecipientKind.Devices when userIds.Count == 0 && platforms.Count == 0
                => new NotificationRecipient(recipient.Kind, userIds, deviceIds, platforms),
            NotificationRecipientKind.Platforms when userIds.Count == 0 && deviceIds.Count == 0
                => new NotificationRecipient(recipient.Kind, userIds, deviceIds, platforms),
            _ => throw new ArgumentException("The recipient selector contains fields that do not match its kind.", nameof(recipient))
        };
    }

    private static void ValidateOptions(NotificationsHubOptions options)
    {
        if (options.MaximumDevices <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumDevices));
        if (options.MaximumTitleLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumTitleLength));
        if (options.MaximumBodyLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumBodyLength));
        if (options.MaximumDataEntries < 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumDataEntries));
        if (options.MaximumActions < 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumActions));
        if (options.MaximumDataKeyLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumDataKeyLength));
        if (options.MaximumDataValueLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumDataValueLength));
        if (options.MaximumActionTitleLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumActionTitleLength));
    }

    private static NotificationMessage SnapshotNotification(NotificationMessage notification)
        => notification with
        {
            Data = Array.AsReadOnly(notification.Data.Select(entry => new NotificationDataEntry(entry.Key, entry.Value)).ToArray()),
            Actions = Array.AsReadOnly(notification.Actions.Select(action => new NotificationAction(action.ActionId, action.Title, action.Uri)).ToArray())
        };

    private static string? NormalizeOptional(string? value, int maximumLength, string parameterName, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) throw new ArgumentException("A value is required.", parameterName);
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength) throw new ArgumentOutOfRangeException(parameterName);
        return normalized;
    }

    private void PublishStateChanged(NotificationsHubSnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        var eventArgs = new HubStateChangedEventArgs<NotificationsHubSnapshot>(snapshot);
        foreach (EventHandler<HubStateChangedEventArgs<NotificationsHubSnapshot>> handler in handlers.GetInvocationList().Cast<EventHandler<HubStateChangedEventArgs<NotificationsHubSnapshot>>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A notifications hub state subscriber failed for hub {HubId} at revision {Revision}.", snapshot.HubId, snapshot.Revision);
            }
        }
    }
}