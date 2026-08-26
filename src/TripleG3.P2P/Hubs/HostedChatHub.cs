using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>Authoritative chat room whose hosts manage membership and moderation.</summary>
public sealed class HostedChatHub : IHostedChatHub
{
    private readonly object _gate = new();
    private readonly HubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedChatHub> _logger;
    private ChatHubSnapshot _snapshot;

    public HostedChatHub(
        Guid hubId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<HostedChatHub>? logger = null)
    {
        HubValidation.ValidateId(hubId, nameof(hubId));
        HubValidation.ValidateId(initialHostMemberId, nameof(initialHostMemberId));
        _options = options ?? new HubOptions();
        HubValidation.ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<HostedChatHub>.Instance;
        var username = HubValidation.NormalizeName(initialHostUsername, _options.MaximumUsernameLength, nameof(initialHostUsername));
        _snapshot = new ChatHubSnapshot(
            hubId,
            1,
            HubValidation.Snapshot([new HubMember(initialHostMemberId, username, HubMemberRole.Host, _timeProvider.GetUtcNow())]),
            HubValidation.Snapshot<HubChatMessage>([]),
            HubValidation.Snapshot<HubNotification>([]));
    }

    public ChatHubSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<HubStateChangedEventArgs<ChatHubSnapshot>>? StateChanged;

    public ChatHubSnapshot AddMember(Guid requesterMemberId, Guid memberId, string username)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        var normalizedUsername = HubValidation.NormalizeName(username, _options.MaximumUsernameLength, nameof(username));
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (current.Members.Count >= _options.MaximumMembers) throw new InvalidOperationException("The chat hub is full.");
            EnsureNewMember(current, memberId, normalizedUsername);
            var members = HubValidation.Snapshot(current.Members.Append(
                new HubMember(memberId, normalizedUsername, HubMemberRole.Member, _timeProvider.GetUtcNow())));
            changed = UpdateMembership(current, members, HubNotificationKind.MemberJoined, requesterMemberId, memberId, $"{normalizedUsername} joined.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public ChatHubSnapshot RemoveMember(Guid requesterMemberId, Guid memberId)
    {
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (requesterMemberId == memberId) throw new InvalidOperationException("Use Leave to remove the requesting member.");
            var member = GetMember(current, memberId);
            EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            changed = UpdateMembership(current, members, HubNotificationKind.MemberRemoved, requesterMemberId, memberId, $"{member.Username} was removed.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public ChatHubSnapshot PromoteMember(Guid requesterMemberId, Guid memberId)
        => ChangeRole(requesterMemberId, memberId, HubMemberRole.Host, HubNotificationKind.MemberPromoted, "was promoted to host.");

    public ChatHubSnapshot DemoteMember(Guid requesterMemberId, Guid memberId)
        => ChangeRole(requesterMemberId, memberId, HubMemberRole.Member, HubNotificationKind.MemberDemoted, "was demoted to member.");

    public ChatHubSnapshot Leave(Guid memberId)
    {
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            changed = UpdateMembership(current, members, HubNotificationKind.MemberLeft, memberId, memberId, $"{member.Username} left.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public HubDispatch SendMessage(Guid senderMemberId, string text)
    {
        var normalizedText = HubValidation.NormalizeMessage(text, _options.MaximumMessageLength);
        ChatHubSnapshot changed;
        HubDispatch dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            var nextRevision = current.Revision + 1;
            var message = new HubChatMessage(current.HubId, nextRevision, Guid.NewGuid(), sender.MemberId, sender.Username, HubAudience.All, Guid.Empty, normalizedText, _timeProvider.GetUtcNow());
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

    private ChatHubSnapshot ChangeRole(
        Guid requesterMemberId,
        Guid memberId,
        HubMemberRole role,
        HubNotificationKind notificationKind,
        string action)
    {
        ChatHubSnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            var member = GetMember(current, memberId);
            if (member.Role == role) return current;
            if (role == HubMemberRole.Member) EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Select(candidate =>
                candidate.MemberId == memberId ? candidate with { Role = role } : candidate));
            changed = UpdateMembership(current, members, notificationKind, requesterMemberId, memberId, $"{member.Username} {action}");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    private ChatHubSnapshot UpdateMembership(
        ChatHubSnapshot current,
        IReadOnlyList<HubMember> members,
        HubNotificationKind kind,
        Guid actorMemberId,
        Guid subjectMemberId,
        string text)
    {
        var notification = new HubNotification(
            Guid.NewGuid(), kind, actorMemberId, subjectMemberId, Guid.Empty, text, members.Count, _timeProvider.GetUtcNow());
        return current with
        {
            Revision = current.Revision + 1,
            Members = members,
            Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
        };
    }

    private static HubMember GetMember(ChatHubSnapshot snapshot, Guid memberId)
        => snapshot.Members.FirstOrDefault(member => member.MemberId == memberId)
            ?? throw new KeyNotFoundException($"Member {memberId} is not in the chat hub.");

    private static void EnsureHost(ChatHubSnapshot snapshot, Guid requesterMemberId)
    {
        var requester = GetMember(snapshot, requesterMemberId);
        if (requester.Role != HubMemberRole.Host) throw new UnauthorizedAccessException("A host role is required.");
    }

    private static void EnsureNewMember(ChatHubSnapshot snapshot, Guid memberId, string username)
    {
        if (snapshot.Members.Any(member => member.MemberId == memberId)) throw new InvalidOperationException($"Member {memberId} is already in the chat hub.");
        if (snapshot.Members.Any(member => string.Equals(member.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Username '{username}' is already in use.");
        }
    }

    private static void EnsureHostRemains(ChatHubSnapshot snapshot, Guid memberId)
    {
        var member = GetMember(snapshot, memberId);
        if (member.Role == HubMemberRole.Host
            && snapshot.Members.Count(candidate => candidate.Role == HubMemberRole.Host) == 1)
        {
            throw new InvalidOperationException("The final host cannot be removed or demoted; remove the hub through its catalog lifecycle.");
        }
    }

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
                _logger.LogWarning(exception, "A hosted chat hub state subscriber failed for hub {HubId} at revision {Revision}.", snapshot.HubId, snapshot.Revision);
            }
        }
    }
}