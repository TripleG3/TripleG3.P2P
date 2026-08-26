using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>Authoritative ownerless chat room for zero or more members.</summary>
public sealed class ChatHub : IOwnerlessChatHub
{
    private readonly object _gate = new();
    private readonly HubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatHub> _logger;
    private ChatHubSnapshot _snapshot;

    public ChatHub(
        Guid hubId,
        HubOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<ChatHub>? logger = null)
    {
        HubValidation.ValidateId(hubId, nameof(hubId));
        _options = options ?? new HubOptions();
        HubValidation.ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ChatHub>.Instance;
        _snapshot = new ChatHubSnapshot(
            hubId,
            0,
            HubValidation.Snapshot<HubMember>([]),
            HubValidation.Snapshot<HubChatMessage>([]),
            HubValidation.Snapshot<HubNotification>([]));
    }

    public ChatHubSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<HubStateChangedEventArgs<ChatHubSnapshot>>? StateChanged;

    public ChatHubSnapshot Join(Guid memberId, string username)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        var normalizedUsername = HubValidation.NormalizeName(username, _options.MaximumUsernameLength, nameof(username));
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            if (current.Members.Count >= _options.MaximumMembers) throw new InvalidOperationException("The chat hub is full.");
            EnsureNewMember(current, memberId, normalizedUsername);
            var members = HubValidation.Snapshot(current.Members.Append(
                new HubMember(memberId, normalizedUsername, HubMemberRole.Member, _timeProvider.GetUtcNow())));
            var notification = CreateNotification(
                HubNotificationKind.MemberJoined,
                memberId,
                memberId,
                $"{normalizedUsername} joined.",
                members.Count);
            changed = current with
            {
                Revision = current.Revision + 1,
                Members = members,
                Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
            };
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public ChatHubSnapshot Leave(Guid memberId)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            var notification = CreateNotification(
                HubNotificationKind.MemberLeft,
                memberId,
                memberId,
                $"{member.Username} left.",
                members.Count);
            changed = current with
            {
                Revision = current.Revision + 1,
                Members = members,
                Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
            };
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public HubDispatch SendMessage(Guid senderMemberId, string text)
    {
        HubValidation.ValidateId(senderMemberId, nameof(senderMemberId));
        var normalizedText = HubValidation.NormalizeMessage(text, _options.MaximumMessageLength);
        ChatHubSnapshot changed;
        HubDispatch dispatch;
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
            changed = current with
            {
                Revision = nextRevision,
                Messages = HubValidation.AppendBounded(current.Messages, message, _options.MessageHistoryCapacity)
            };
            dispatch = new HubDispatch(
                message,
                HubValidation.Snapshot(current.Members.Where(member => member.MemberId != senderMemberId).Select(member => member.MemberId)),
                changed.Revision);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public HubDispatch<TMessage> RouteMessage<TMessage>(Guid senderMemberId, TMessage message)
    {
        HubValidation.ValidateId(senderMemberId, nameof(senderMemberId));
        ArgumentNullException.ThrowIfNull(message);
        ChatHubSnapshot changed;
        HubDispatch<TMessage> dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            _ = GetMember(current, senderMemberId);
            var nextRevision = current.Revision + 1;
            changed = current with { Revision = nextRevision };
            dispatch = new HubDispatch<TMessage>(
                new HubDispatchMetadata(
                    current.HubId,
                    nextRevision,
                    senderMemberId,
                    HubAudience.All,
                    Guid.Empty,
                    _timeProvider.GetUtcNow()),
                message,
                HubValidation.Snapshot(current.Members.Where(member => member.MemberId != senderMemberId).Select(member => member.MemberId)));
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    private static HubMember GetMember(ChatHubSnapshot snapshot, Guid memberId)
        => snapshot.Members.FirstOrDefault(member => member.MemberId == memberId)
            ?? throw new KeyNotFoundException($"Member {memberId} is not in the chat hub.");

    private static void EnsureNewMember(ChatHubSnapshot snapshot, Guid memberId, string username)
    {
        if (snapshot.Members.Any(member => member.MemberId == memberId))
        {
            throw new InvalidOperationException($"Member {memberId} is already in the chat hub.");
        }

        if (snapshot.Members.Any(member => string.Equals(member.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Username '{username}' is already in use.");
        }
    }

    private HubNotification CreateNotification(
        HubNotificationKind kind,
        Guid actorMemberId,
        Guid subjectMemberId,
        string text,
        int memberCount)
        => new(Guid.NewGuid(), kind, actorMemberId, subjectMemberId, Guid.Empty, text, memberCount, _timeProvider.GetUtcNow());

    private void PublishStateChanged(ChatHubSnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        var eventArgs = new HubStateChangedEventArgs<ChatHubSnapshot>(snapshot);
        foreach (EventHandler<HubStateChangedEventArgs<ChatHubSnapshot>> handler in handlers.GetInvocationList().Cast<EventHandler<HubStateChangedEventArgs<ChatHubSnapshot>>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A chat hub state subscriber failed for hub {HubId} at revision {Revision}.", snapshot.HubId, snapshot.Revision);
            }
        }
    }
}