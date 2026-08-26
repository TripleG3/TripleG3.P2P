using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>
/// Authoritative multiparty video chat state and routing policy. The host owns capture, encoding,
/// RTP sender/receiver instances, media protection, decoding, rendering, and playback.
/// </summary>
public sealed class VideoChatHub : IVideoChatHub
{
    private readonly object _gate = new();
    private readonly HubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VideoChatHub> _logger;
    private readonly object _eventGate = new();
    private readonly SortedDictionary<long, VideoChatHubSnapshot> _pendingEvents = [];
    private CancellationTokenSource _routeRevocation = new();
    private long _publishedRevision;
    private bool _publishingEvents;
    private VideoChatHubSnapshot _snapshot;

    public VideoChatHub(
        Guid hubId,
        HubOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<VideoChatHub>? logger = null)
    {
        HubValidation.ValidateId(hubId, nameof(hubId));
        _options = options ?? new HubOptions();
        HubValidation.ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VideoChatHub>.Instance;
        _snapshot = new VideoChatHubSnapshot(
            hubId,
            0,
            0,
            HubValidation.Snapshot<VideoChatMember>([]),
            HubValidation.Snapshot<HubChatMessage>([]),
            HubValidation.Snapshot<HubNotification>([]));
    }

    public VideoChatHubSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<HubStateChangedEventArgs<VideoChatHubSnapshot>>? StateChanged;

    public VideoChatHubSnapshot Join(Guid memberId, string username)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        var normalizedUsername = HubValidation.NormalizeName(username, _options.MaximumUsernameLength, nameof(username));
        VideoChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            if (current.Members.Count >= _options.MaximumMembers) throw new InvalidOperationException("The video chat hub is full.");
            EnsureNewMember(current, memberId, normalizedUsername);
            var members = HubValidation.Snapshot(current.Members.Append(
                new VideoChatMember(memberId, normalizedUsername, false, false, _timeProvider.GetUtcNow())));
            changed = UpdateRoutingState(
                current,
                members,
                HubNotificationKind.MemberJoined,
                memberId,
                $"{normalizedUsername} joined.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public VideoChatHubSnapshot Leave(Guid memberId)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        VideoChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            changed = UpdateRoutingState(
                current,
                members,
                HubNotificationKind.MemberLeft,
                memberId,
                $"{member.Username} left.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public VideoChatHubSnapshot SetCameraEnabled(Guid memberId, bool enabled)
        => SetMediaState(memberId, enabled, true);

    public VideoChatHubSnapshot SetMicrophoneEnabled(Guid memberId, bool enabled)
        => SetMediaState(memberId, enabled, false);

    public VideoChatDispatch<HubChatMessage> SendMessage(Guid senderMemberId, string text)
    {
        HubValidation.ValidateId(senderMemberId, nameof(senderMemberId));
        var normalizedText = HubValidation.NormalizeMessage(text, _options.MaximumMessageLength);
        VideoChatHubSnapshot changed;
        VideoChatDispatch<HubChatMessage> dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            var nextRevision = current.Revision + 1;
            var message = new HubChatMessage(
                current.HubId,
                nextRevision,
                Guid.NewGuid(),
                sender.MemberId,
                sender.Username,
                HubAudience.All,
                Guid.Empty,
                normalizedText,
                _timeProvider.GetUtcNow());
            var route = CreateRecipientRoute(current, senderMemberId, VideoChatMediaKind.None);
            changed = current with
            {
                Revision = nextRevision,
                Messages = HubValidation.AppendBounded(current.Messages, message, _options.MessageHistoryCapacity)
            };
            dispatch = new VideoChatDispatch<HubChatMessage>(
                new HubDispatchMetadata(current.HubId, nextRevision, senderMemberId, HubAudience.All, Guid.Empty, message.SentAt),
                message,
                route);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public VideoChatDispatch<TMessage> RouteMessage<TMessage>(Guid senderMemberId, TMessage message)
    {
        HubValidation.ValidateId(senderMemberId, nameof(senderMemberId));
        ArgumentNullException.ThrowIfNull(message);
        VideoChatHubSnapshot changed;
        VideoChatDispatch<TMessage> dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            _ = GetMember(current, senderMemberId);
            var nextRevision = current.Revision + 1;
            var createdAt = _timeProvider.GetUtcNow();
            var route = CreateRecipientRoute(current, senderMemberId, VideoChatMediaKind.None);
            changed = current with { Revision = nextRevision };
            dispatch = new VideoChatDispatch<TMessage>(
                new HubDispatchMetadata(current.HubId, nextRevision, senderMemberId, HubAudience.All, Guid.Empty, createdAt),
                message,
                route);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public VideoChatRecipientRoute GetMediaRoute(Guid senderMemberId, VideoChatMediaKind mediaKind)
    {
        if (mediaKind is VideoChatMediaKind.None || !Enum.IsDefined(mediaKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaKind));
        }

        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            EnsurePublishingEnabled(sender, mediaKind);
            return CreateRecipientRoute(current, senderMemberId, mediaKind);
        }
    }

    public bool IsRouteCurrent(VideoChatRecipientRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_gate)
        {
            var current = _snapshot;
            if (!Enum.IsDefined(route.MediaKind)) return false;
            if (route.RevocationToken.IsCancellationRequested) return false;
            if (route.HubId != current.HubId || route.RoutingRevision != current.RoutingRevision) return false;
            try
            {
                var sender = GetMember(current, route.SenderMemberId);
                if (route.MediaKind != VideoChatMediaKind.None) EnsurePublishingEnabled(sender, route.MediaKind);
                var expected = CreateRecipientRoute(current, route.SenderMemberId, route.MediaKind);
                return expected.RecipientMemberIds.Order().SequenceEqual(route.RecipientMemberIds.Order());
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }

    private VideoChatHubSnapshot SetMediaState(Guid memberId, bool enabled, bool camera)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        VideoChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            if ((camera ? member.IsCameraEnabled : member.IsMicrophoneEnabled) == enabled) return current;
            var members = HubValidation.Snapshot(current.Members.Select(candidate =>
                candidate.MemberId != memberId
                    ? candidate
                    : camera
                        ? candidate with { IsCameraEnabled = enabled }
                        : candidate with { IsMicrophoneEnabled = enabled }));
            changed = UpdateRoutingState(
                current,
                members,
                camera ? HubNotificationKind.CameraStateChanged : HubNotificationKind.MicrophoneStateChanged,
                memberId,
                $"{member.Username} {(camera ? "camera" : "microphone")} {(enabled ? "enabled" : "disabled")}.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    private VideoChatHubSnapshot UpdateRoutingState(
        VideoChatHubSnapshot current,
        IReadOnlyList<VideoChatMember> members,
        HubNotificationKind kind,
        Guid memberId,
        string text)
    {
        var nextRevision = current.Revision + 1;
        var notification = new HubNotification(
            Guid.NewGuid(),
            kind,
            memberId,
            memberId,
            Guid.Empty,
            text,
            members.Count,
            _timeProvider.GetUtcNow());
        var previousRevocation = _routeRevocation;
        _routeRevocation = new CancellationTokenSource();
        previousRevocation.Cancel();
        previousRevocation.Dispose();
        return current with
        {
            Revision = nextRevision,
            RoutingRevision = current.RoutingRevision + 1,
            Members = members,
            Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
        };
    }

    private VideoChatRecipientRoute CreateRecipientRoute(
        VideoChatHubSnapshot snapshot,
        Guid senderMemberId,
        VideoChatMediaKind mediaKind)
        => new(
            snapshot.HubId,
            senderMemberId,
            mediaKind,
            HubValidation.Snapshot(snapshot.Members.Where(member => member.MemberId != senderMemberId).Select(member => member.MemberId)),
            snapshot.RoutingRevision,
            _routeRevocation.Token);

    private static VideoChatMember GetMember(VideoChatHubSnapshot snapshot, Guid memberId)
        => snapshot.Members.FirstOrDefault(member => member.MemberId == memberId)
            ?? throw new KeyNotFoundException($"Member {memberId} is not in the video chat hub.");

    private static void EnsureNewMember(VideoChatHubSnapshot snapshot, Guid memberId, string username)
    {
        if (snapshot.Members.Any(member => member.MemberId == memberId))
        {
            throw new InvalidOperationException($"Member {memberId} is already in the video chat hub.");
        }
        if (snapshot.Members.Any(member => string.Equals(member.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Username '{username}' is already in use.");
        }
    }

    private static void EnsurePublishingEnabled(VideoChatMember sender, VideoChatMediaKind mediaKind)
    {
        if (mediaKind.HasFlag(VideoChatMediaKind.Audio) && !sender.IsMicrophoneEnabled)
        {
            throw new InvalidOperationException("The sender microphone is disabled.");
        }
        if (mediaKind.HasFlag(VideoChatMediaKind.Video) && !sender.IsCameraEnabled)
        {
            throw new InvalidOperationException("The sender camera is disabled.");
        }
    }

    private void PublishStateChanged(VideoChatHubSnapshot snapshot)
    {
        lock (_eventGate)
        {
            _pendingEvents[snapshot.Revision] = snapshot;
            if (_publishingEvents) return;
            _publishingEvents = true;
        }

        while (true)
        {
            VideoChatHubSnapshot? next;
            lock (_eventGate)
            {
                if (!_pendingEvents.Remove(_publishedRevision + 1, out next))
                {
                    _publishingEvents = false;
                    return;
                }
                _publishedRevision = next.Revision;
            }

            var handlers = StateChanged;
            if (handlers is null) continue;
            var eventArgs = new HubStateChangedEventArgs<VideoChatHubSnapshot>(next);
            foreach (EventHandler<HubStateChangedEventArgs<VideoChatHubSnapshot>> handler in handlers.GetInvocationList().Cast<EventHandler<HubStateChangedEventArgs<VideoChatHubSnapshot>>>())
            {
                try
                {
                    handler(this, eventArgs);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "A video chat hub state subscriber failed for hub {HubId} at revision {Revision}.", next.HubId, next.Revision);
                }
            }
        }
    }
}