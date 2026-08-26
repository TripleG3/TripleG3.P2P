using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>Authoritative hosted gaming lobby with teams and scoped chat/audio routing policy.</summary>
public sealed class GamingLobbyHub : IGamingLobbyHub
{
    private readonly object _gate = new();
    private readonly HubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GamingLobbyHub> _logger;
    private readonly Dictionary<Guid, IReadOnlyList<HubChatMessage>> _teamMessages = [];
    private GamingLobbySnapshot _snapshot;

    public GamingLobbyHub(
        Guid lobbyId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<GamingLobbyHub>? logger = null)
    {
        HubValidation.ValidateId(lobbyId, nameof(lobbyId));
        HubValidation.ValidateId(initialHostMemberId, nameof(initialHostMemberId));
        _options = options ?? new HubOptions();
        HubValidation.ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GamingLobbyHub>.Instance;
        var username = HubValidation.NormalizeName(initialHostUsername, _options.MaximumUsernameLength, nameof(initialHostUsername));
        _snapshot = new GamingLobbySnapshot(
            lobbyId,
            1,
            HubValidation.Snapshot([new GamingLobbyMember(initialHostMemberId, username, HubMemberRole.Host, Guid.Empty, _timeProvider.GetUtcNow())]),
            HubValidation.Snapshot<GamingLobbyTeam>([]),
            HubValidation.Snapshot<HubChatMessage>([]),
            HubValidation.Snapshot<HubNotification>([]),
            GamingLobbyAudioPolicy.AllAndTeam);
    }

    public GamingLobbySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<HubStateChangedEventArgs<GamingLobbySnapshot>>? StateChanged;

    public GamingLobbySnapshot AddMember(Guid requesterMemberId, Guid memberId, string username)
    {
        HubValidation.ValidateId(memberId, nameof(memberId));
        var normalizedUsername = HubValidation.NormalizeName(username, _options.MaximumUsernameLength, nameof(username));
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (current.Members.Count >= _options.MaximumMembers) throw new InvalidOperationException("The gaming lobby is full.");
            EnsureNewMember(current, memberId, normalizedUsername);
            var members = HubValidation.Snapshot(current.Members.Append(
                new GamingLobbyMember(memberId, normalizedUsername, HubMemberRole.Member, Guid.Empty, _timeProvider.GetUtcNow())));
            changed = UpdateMembership(current, members, current.Teams, HubNotificationKind.MemberJoined, requesterMemberId, memberId, Guid.Empty, $"{normalizedUsername} joined.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot RemoveMember(Guid requesterMemberId, Guid memberId)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (requesterMemberId == memberId) throw new InvalidOperationException("Use Leave to remove the requesting member.");
            var member = GetMember(current, memberId);
            EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            var teams = RecountTeams(current.Teams, members);
            changed = UpdateMembership(current, members, teams, HubNotificationKind.MemberRemoved, requesterMemberId, memberId, member.TeamId, $"{member.Username} was removed.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot PromoteMember(Guid requesterMemberId, Guid memberId)
        => ChangeRole(requesterMemberId, memberId, HubMemberRole.Host, HubNotificationKind.MemberPromoted, "was promoted to host.");

    public GamingLobbySnapshot DemoteMember(Guid requesterMemberId, Guid memberId)
        => ChangeRole(requesterMemberId, memberId, HubMemberRole.Member, HubNotificationKind.MemberDemoted, "was demoted to member.");

    public GamingLobbySnapshot Leave(Guid memberId)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Where(candidate => candidate.MemberId != memberId));
            var teams = RecountTeams(current.Teams, members);
            changed = UpdateMembership(current, members, teams, HubNotificationKind.MemberLeft, memberId, memberId, member.TeamId, $"{member.Username} left.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot AddTeam(Guid requesterMemberId, Guid teamId, string name)
    {
        HubValidation.ValidateId(teamId, nameof(teamId));
        var normalizedName = HubValidation.NormalizeName(name, _options.MaximumTeamNameLength, nameof(name));
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (current.Teams.Any(team => team.TeamId == teamId)) throw new InvalidOperationException($"Team {teamId} already exists.");
            if (current.Teams.Any(team => string.Equals(team.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Team name '{normalizedName}' is already in use.");
            }

            var teams = HubValidation.Snapshot(current.Teams.Append(new GamingLobbyTeam(teamId, normalizedName, 0)));
            _teamMessages.Add(teamId, HubValidation.Snapshot<HubChatMessage>([]));
            changed = UpdateMembership(current, current.Members, teams, HubNotificationKind.TeamAdded, requesterMemberId, Guid.Empty, teamId, $"Team {normalizedName} was added.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot RemoveTeam(Guid requesterMemberId, Guid teamId)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            var team = GetTeam(current, teamId);
            if (team.MemberCount > 0) throw new InvalidOperationException("A nonempty team cannot be removed.");
            var teams = HubValidation.Snapshot(current.Teams.Where(candidate => candidate.TeamId != teamId));
            _teamMessages.Remove(teamId);
            changed = UpdateMembership(current, current.Members, teams, HubNotificationKind.TeamRemoved, requesterMemberId, Guid.Empty, teamId, $"Team {team.Name} was removed.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot AssignMemberToTeam(Guid requesterMemberId, Guid memberId, Guid teamId)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            var member = GetMember(current, memberId);
            _ = GetTeam(current, teamId);
            if (member.TeamId == teamId) return current;
            var members = HubValidation.Snapshot(current.Members.Select(candidate =>
                candidate.MemberId == memberId ? candidate with { TeamId = teamId } : candidate));
            var teams = RecountTeams(current.Teams, members);
            changed = UpdateMembership(current, members, teams, HubNotificationKind.TeamAssigned, requesterMemberId, memberId, teamId, $"{member.Username} joined a team.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot UnassignMemberFromTeam(Guid requesterMemberId, Guid memberId)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            var member = GetMember(current, memberId);
            if (member.TeamId == Guid.Empty) return current;
            var previousTeamId = member.TeamId;
            var members = HubValidation.Snapshot(current.Members.Select(candidate =>
                candidate.MemberId == memberId ? candidate with { TeamId = Guid.Empty } : candidate));
            var teams = RecountTeams(current.Teams, members);
            changed = UpdateMembership(current, members, teams, HubNotificationKind.TeamUnassigned, requesterMemberId, memberId, previousTeamId, $"{member.Username} left a team.");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public GamingLobbySnapshot SetAudioPolicy(Guid requesterMemberId, GamingLobbyAudioPolicy policy)
    {
        if (!Enum.IsDefined(policy) && policy != GamingLobbyAudioPolicy.AllAndTeam) throw new ArgumentOutOfRangeException(nameof(policy));
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            if (current.AudioPolicy == policy) return current;
            var notification = CreateNotification(
                HubNotificationKind.AudioPolicyChanged,
                requesterMemberId,
                Guid.Empty,
                Guid.Empty,
                $"Audio policy changed to {policy}.",
                current.Members.Count);
            changed = current with
            {
                Revision = current.Revision + 1,
                AudioPolicy = policy,
                Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
            };
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    public HubDispatch SendChat(Guid senderMemberId, HubAudience audience, Guid teamId, string text)
    {
        var normalizedText = HubValidation.NormalizeMessage(text, _options.MaximumMessageLength);
        GamingLobbySnapshot changed;
        HubDispatch dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            var recipients = GetRecipients(current, sender, audience, teamId);
            var routedTeamId = audience == HubAudience.Team ? sender.TeamId : Guid.Empty;
            var nextRevision = current.Revision + 1;
            var message = new HubChatMessage(current.LobbyId, nextRevision, Guid.NewGuid(), sender.MemberId, sender.Username, audience, routedTeamId, normalizedText, _timeProvider.GetUtcNow());
            changed = audience == HubAudience.All
                ? current with
                {
                    Revision = nextRevision,
                    Messages = HubValidation.AppendBounded(current.Messages, message, _options.MessageHistoryCapacity)
                }
                : current with { Revision = nextRevision };
            if (audience == HubAudience.Team)
            {
                _teamMessages[routedTeamId] = HubValidation.AppendBounded(
                    _teamMessages[routedTeamId],
                    message,
                    _options.MessageHistoryCapacity);
            }
            dispatch = new HubDispatch(message, recipients, changed.Revision);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public HubDispatch<TMessage> RouteMessage<TMessage>(
        Guid senderMemberId,
        HubAudience audience,
        Guid teamId,
        TMessage message)
    {
        HubValidation.ValidateId(senderMemberId, nameof(senderMemberId));
        ArgumentNullException.ThrowIfNull(message);
        GamingLobbySnapshot changed;
        HubDispatch<TMessage> dispatch;
        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            var recipients = GetRecipients(current, sender, audience, teamId);
            var routedTeamId = audience == HubAudience.Team ? sender.TeamId : Guid.Empty;
            var nextRevision = current.Revision + 1;
            changed = current with { Revision = nextRevision };
            dispatch = new HubDispatch<TMessage>(
                new HubDispatchMetadata(
                    current.LobbyId,
                    nextRevision,
                    senderMemberId,
                    audience,
                    routedTeamId,
                    _timeProvider.GetUtcNow()),
                message,
                recipients);
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return dispatch;
    }

    public HubAudioRoute GetAudioRoute(Guid senderMemberId, HubAudience audience, Guid teamId)
    {
        lock (_gate)
        {
            var current = _snapshot;
            var sender = GetMember(current, senderMemberId);
            EnsureAudioAllowed(current.AudioPolicy, audience);
            var recipients = GetRecipients(current, sender, audience, teamId);
            return new HubAudioRoute(current.LobbyId, senderMemberId, audience, audience == HubAudience.Team ? sender.TeamId : Guid.Empty, recipients, current.Revision);
        }
    }

    public bool IsAudioRouteCurrent(HubAudioRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_gate)
        {
            var current = _snapshot;
            if (route.LobbyId != current.LobbyId || route.Revision != current.Revision) return false;
            try
            {
                var sender = GetMember(current, route.SenderMemberId);
                EnsureAudioAllowed(current.AudioPolicy, route.Audience);
                var recipients = GetRecipients(current, sender, route.Audience, route.TeamId);
                return recipients.Order().SequenceEqual(route.RecipientMemberIds.Order());
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<HubChatMessage> GetMessagesForMember(Guid memberId)
    {
        lock (_gate)
        {
            var current = _snapshot;
            var member = GetMember(current, memberId);
            var teamMessages = member.TeamId == Guid.Empty
                ? HubValidation.Snapshot<HubChatMessage>([])
                : _teamMessages[member.TeamId];
            return HubValidation.Snapshot(current.Messages.Concat(teamMessages).OrderBy(message => message.Revision));
        }
    }

    private GamingLobbySnapshot ChangeRole(Guid requesterMemberId, Guid memberId, HubMemberRole role, HubNotificationKind kind, string action)
    {
        GamingLobbySnapshot changed;
        lock (_gate)
        {
            var current = _snapshot;
            EnsureHost(current, requesterMemberId);
            var member = GetMember(current, memberId);
            if (member.Role == role) return current;
            if (role == HubMemberRole.Member) EnsureHostRemains(current, memberId);
            var members = HubValidation.Snapshot(current.Members.Select(candidate =>
                candidate.MemberId == memberId ? candidate with { Role = role } : candidate));
            changed = UpdateMembership(current, members, current.Teams, kind, requesterMemberId, memberId, member.TeamId, $"{member.Username} {action}");
            Volatile.Write(ref _snapshot, changed);
        }

        PublishStateChanged(changed);
        return changed;
    }

    private GamingLobbySnapshot UpdateMembership(
        GamingLobbySnapshot current,
        IReadOnlyList<GamingLobbyMember> members,
        IReadOnlyList<GamingLobbyTeam> teams,
        HubNotificationKind kind,
        Guid actorMemberId,
        Guid subjectMemberId,
        Guid teamId,
        string text)
    {
        var notification = CreateNotification(kind, actorMemberId, subjectMemberId, teamId, text, members.Count);
        return current with
        {
            Revision = current.Revision + 1,
            Members = members,
            Teams = teams,
            Notifications = HubValidation.AppendBounded(current.Notifications, notification, _options.NotificationHistoryCapacity)
        };
    }

    private HubNotification CreateNotification(
        HubNotificationKind kind,
        Guid actorMemberId,
        Guid subjectMemberId,
        Guid teamId,
        string text,
        int memberCount)
        => new(Guid.NewGuid(), kind, actorMemberId, subjectMemberId, teamId, text, memberCount, _timeProvider.GetUtcNow());

    private static IReadOnlyList<GamingLobbyTeam> RecountTeams(
        IReadOnlyList<GamingLobbyTeam> teams,
        IReadOnlyList<GamingLobbyMember> members)
        => HubValidation.Snapshot(teams.Select(team => team with
        {
            MemberCount = members.Count(member => member.TeamId == team.TeamId)
        }));

    private static GamingLobbyMember GetMember(GamingLobbySnapshot snapshot, Guid memberId)
        => snapshot.Members.FirstOrDefault(member => member.MemberId == memberId)
            ?? throw new KeyNotFoundException($"Member {memberId} is not in the gaming lobby.");

    private static GamingLobbyTeam GetTeam(GamingLobbySnapshot snapshot, Guid teamId)
    {
        HubValidation.ValidateId(teamId, nameof(teamId));
        return snapshot.Teams.FirstOrDefault(team => team.TeamId == teamId)
            ?? throw new KeyNotFoundException($"Team {teamId} is not in the gaming lobby.");
    }

    private static void EnsureHost(GamingLobbySnapshot snapshot, Guid requesterMemberId)
    {
        var requester = GetMember(snapshot, requesterMemberId);
        if (requester.Role != HubMemberRole.Host) throw new UnauthorizedAccessException("A host role is required.");
    }

    private static void EnsureNewMember(GamingLobbySnapshot snapshot, Guid memberId, string username)
    {
        if (snapshot.Members.Any(member => member.MemberId == memberId)) throw new InvalidOperationException($"Member {memberId} is already in the gaming lobby.");
        if (snapshot.Members.Any(member => string.Equals(member.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Username '{username}' is already in use.");
        }
    }

    private static void EnsureHostRemains(GamingLobbySnapshot snapshot, Guid memberId)
    {
        var member = GetMember(snapshot, memberId);
        if (member.Role == HubMemberRole.Host
            && snapshot.Members.Count(candidate => candidate.Role == HubMemberRole.Host) == 1)
        {
            throw new InvalidOperationException("The final host cannot be removed or demoted; remove the lobby through its catalog lifecycle.");
        }
    }

    private static IReadOnlyList<Guid> GetRecipients(
        GamingLobbySnapshot snapshot,
        GamingLobbyMember sender,
        HubAudience audience,
        Guid teamId)
        => audience switch
        {
            HubAudience.All when teamId == Guid.Empty => HubValidation.Snapshot(
                snapshot.Members.Where(member => member.MemberId != sender.MemberId).Select(member => member.MemberId)),
            HubAudience.All => throw new ArgumentException("TeamId must be empty for the all-member audience.", nameof(teamId)),
            HubAudience.Team when sender.TeamId == Guid.Empty => throw new InvalidOperationException("The sender is not assigned to a team."),
            HubAudience.Team when teamId != sender.TeamId => throw new UnauthorizedAccessException("The sender may route only to their assigned team."),
            HubAudience.Team => HubValidation.Snapshot(
                snapshot.Members.Where(member => member.MemberId != sender.MemberId && member.TeamId == sender.TeamId).Select(member => member.MemberId)),
            _ => throw new ArgumentOutOfRangeException(nameof(audience))
        };

    private static void EnsureAudioAllowed(GamingLobbyAudioPolicy policy, HubAudience audience)
    {
        var required = audience switch
        {
            HubAudience.All => GamingLobbyAudioPolicy.All,
            HubAudience.Team => GamingLobbyAudioPolicy.Team,
            _ => throw new ArgumentOutOfRangeException(nameof(audience))
        };
        if (!policy.HasFlag(required)) throw new InvalidOperationException($"{audience} audio is disabled.");
    }

    private void PublishStateChanged(GamingLobbySnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        var eventArgs = new HubStateChangedEventArgs<GamingLobbySnapshot>(snapshot);
        foreach (EventHandler<HubStateChangedEventArgs<GamingLobbySnapshot>> handler in handlers.GetInvocationList().Cast<EventHandler<HubStateChangedEventArgs<GamingLobbySnapshot>>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A gaming lobby state subscriber failed for lobby {LobbyId} at revision {Revision}.", snapshot.LobbyId, snapshot.Revision);
            }
        }
    }
}