using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Hubs.Internal;

namespace TripleG3.P2P.Hubs;

/// <summary>
/// Maintains an in-memory connected-device view and produces route plans for host-published messages
/// and live-session control. The host owns trust, network publication, and all data-plane behavior.
/// </summary>
public sealed class ConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>
    : IConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>
    where TDeviceDescriptor : notnull
    where TConnectionRoute : notnull
    where TStreamDescriptor : notnull
{
    private readonly object gate = new();
    private readonly object eventGate = new();
    private readonly ConnectedDeviceHubOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>> logger;
    private readonly Dictionary<Guid, ConnectionEntry> connections = [];
    private readonly Queue<Guid> retiredSessionIds = [];
    private readonly HashSet<Guid> retiredSessionIdSet = [];
    private readonly SortedDictionary<long, PendingStateChange> pendingEvents = [];
    private ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> snapshot;
    private long publishedRevision;
    private bool publishingEvents;
    private bool disposed;

    public ConnectedDeviceHub(
        Guid hubId,
        ConnectedDeviceHubOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<ConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>>? logger = null)
    {
        ValidateId(hubId, nameof(hubId));
        this.options = options ?? new ConnectedDeviceHubOptions();
        ValidateOptions(this.options);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<ConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>>.Instance;
        snapshot = new ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>(hubId, 0, 0, [], [], []);
    }

    public ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Snapshot
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref snapshot);
        }
    }

    public event EventHandler<ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>>? StateChanged;

    public ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Connect(
        DeviceConnection connection,
        TDeviceDescriptor descriptor,
        TConnectionRoute route)
    {
        ValidateConnection(connection);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(route);
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> changed;
        IReadOnlyList<DeviceMembershipChange> changes;
        ConnectionEntry? replacedEntry = null;
        lock (gate)
        {
            ThrowIfDisposed();
            ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current = snapshot;
            if (connections.TryGetValue(connection.DeviceId, out ConnectionEntry? existing) &&
                existing.Device.ConnectionId == connection.ConnectionId)
            {
                if (!EqualityComparer<TDeviceDescriptor>.Default.Equals(existing.Device.Descriptor, descriptor) ||
                    !EqualityComparer<TConnectionRoute>.Default.Equals(existing.Device.Route, route))
                {
                    throw new InvalidOperationException("An active connection cannot change its descriptor or route. Use a new connection identifier.");
                }
                return current;
            }
            if (current.Devices.All(device => device.DeviceId != connection.DeviceId) &&
                current.Devices.Count >= options.MaximumConnectedDevices)
            {
                throw new InvalidOperationException("The connected-device hub is full.");
            }
            if (connections.Values.Any(entry => entry.Device.ConnectionId == connection.ConnectionId))
            {
                throw new InvalidOperationException("The connection identifier is already active for another device.");
            }

            long revision = current.Revision + 1;
            long membershipRevision = current.MembershipRevision + 1;
            DateTimeOffset now = timeProvider.GetUtcNow();
            var pendingChanges = new List<DeviceMembershipChange>(2);
            IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> sessions = current.Sessions;
            if (existing is not null)
            {
                replacedEntry = existing;
                pendingChanges.Add(new DeviceMembershipChange(
                    Guid.NewGuid(),
                    DeviceMembershipChangeKind.Reconnected,
                    existing.Device.DeviceId,
                    connection.ConnectionId,
                    existing.Device.ConnectionId,
                    membershipRevision,
                    now));
                sessions = FailSessionsForConnection(current.Sessions, new DeviceConnection(existing.Device.DeviceId, existing.Device.ConnectionId), revision, now);
            }

            var device = new ConnectedDevice<TDeviceDescriptor, TConnectionRoute>(
                connection.DeviceId,
                connection.ConnectionId,
                descriptor,
                route,
                now);
            connections[connection.DeviceId] = new ConnectionEntry(device);
            if (existing is null)
            {
                pendingChanges.Add(new DeviceMembershipChange(
                    Guid.NewGuid(),
                    DeviceMembershipChangeKind.Joined,
                    connection.DeviceId,
                    connection.ConnectionId,
                    Guid.Empty,
                    membershipRevision,
                    now));
            }
            changes = Array.AsReadOnly(pendingChanges.ToArray());
            IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> sessionDispatches =
                CreateFailureDispatchesForConnection(current.Sessions, new DeviceConnection(existing?.Device.DeviceId ?? Guid.Empty, existing?.Device.ConnectionId ?? Guid.Empty), sessions);
            foreach (LiveSessionSnapshot<TStreamDescriptor> failed in sessions.Where(session => session.IsTerminal)) RetireTerminalSessionId(failed);
            changed = CreateSnapshot(
                current,
                revision,
                membershipRevision,
                connections.Values.Select(entry => entry.Device),
                sessions,
                AppendMembershipHistory(current.MembershipHistory, pendingChanges));
            CommitLocked(changed, changes, sessionDispatches);
        }

        replacedEntry?.Revoke(logger);
        PublishStateChanged();
        return changed;
    }

    public ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Leave(DeviceConnection connection)
        => RemoveConnection(connection, DeviceMembershipChangeKind.Left);

    public ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Disconnect(DeviceConnection connection)
        => RemoveConnection(connection, DeviceMembershipChangeKind.Disconnected);

    public IReadOnlyList<ConnectedDevice<TDeviceDescriptor, TConnectionRoute>> GetConnectedDevices()
        => Snapshot.Devices;

    public bool TryGetConnectedDevice(Guid deviceId, out ConnectedDevice<TDeviceDescriptor, TConnectionRoute>? device)
    {
        ValidateId(deviceId, nameof(deviceId));
        device = Snapshot.Devices.FirstOrDefault(candidate => candidate.DeviceId == deviceId);
        return device is not null;
    }

    public bool IsConnected(DeviceConnection connection)
    {
        ValidateConnection(connection);
        lock (gate)
        {
            ThrowIfDisposed();
            return IsCurrentConnectionLocked(connection);
        }
    }

    public bool IsRouteCurrent(ConnectedDeviceRoute<TConnectionRoute> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (gate)
        {
            ThrowIfDisposed();
            return route.HubId == snapshot.HubId &&
                !route.RevocationToken.IsCancellationRequested &&
                connections.TryGetValue(route.DeviceId, out ConnectionEntry? entry) &&
                entry.Device.ConnectionId == route.ConnectionId;
        }
    }

    public ConnectedDeviceDispatch<TMessage, TConnectionRoute> RouteTo<TMessage>(
        DeviceConnection sender,
        Guid recipientDeviceId,
        TMessage message)
        where TMessage : notnull
    {
        ValidateConnection(sender);
        ValidateId(recipientDeviceId, nameof(recipientDeviceId));
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureCurrentConnectionLocked(sender);
            ConnectionEntry recipient = GetConnectionLocked(recipientDeviceId);
            return CreateDispatch(sender, message, [CreateRoute(recipient)]);
        }
    }

    public ConnectedDeviceDispatch<TMessage, TConnectionRoute> Broadcast<TMessage>(
        DeviceConnection sender,
        TMessage message)
        where TMessage : notnull
    {
        ValidateConnection(sender);
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureCurrentConnectionLocked(sender);
            ConnectedDeviceRoute<TConnectionRoute>[] routes = connections.Values
                .Where(entry => entry.Device.DeviceId != sender.DeviceId)
                .OrderBy(entry => entry.Device.DeviceId)
                .Select(CreateRoute)
                .ToArray();
            return CreateDispatch(sender, message, routes);
        }
    }

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> OfferSession(
        DeviceConnection origin,
        Guid remoteDeviceId,
        Guid sessionId,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams)
    {
        ValidateConnection(origin);
        ValidateId(remoteDeviceId, nameof(remoteDeviceId));
        ValidateId(sessionId, nameof(sessionId));
        IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> offered = ValidateStreams(streams);
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> changed;
        ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> dispatch;
        lock (gate)
        {
            ThrowIfDisposed();
            ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current = snapshot;
            EnsureCurrentConnectionLocked(origin);
            ConnectionEntry remote = GetConnectionLocked(remoteDeviceId);
            if (origin.DeviceId == remoteDeviceId) throw new InvalidOperationException("A live session requires two different devices.");
            if (current.Sessions.Any(session => session.SessionId == sessionId) || retiredSessionIdSet.Contains(sessionId)) throw new InvalidOperationException("The live session identifier was already used.");
            if (current.Sessions.Count(session => !session.IsTerminal) >= options.MaximumSessions) throw new InvalidOperationException("The live-session limit has been reached.");
            DateTimeOffset now = timeProvider.GetUtcNow();
            long revision = current.Revision + 1;
            var session = new LiveSessionSnapshot<TStreamDescriptor>(
                sessionId,
                origin,
                new DeviceConnection(remote.Device.DeviceId, remote.Device.ConnectionId),
                LiveSessionState.Offered,
                offered,
                [],
                string.Empty,
                revision,
                now,
                now);
            changed = CreateSnapshot(current, revision, current.MembershipRevision, current.Devices, AppendSession(current.Sessions, session), current.MembershipHistory);
            CommitLocked(changed, [], []);
            dispatch = CreateDispatch(origin, new LiveSessionControl<TStreamDescriptor>(LiveSessionControlKind.Offer, session), [CreateRoute(remote)]);
        }
        PublishStateChanged();
        return dispatch;
    }

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> AnswerSession(
        DeviceConnection responder,
        Guid sessionId,
        LiveSessionAnswer answer,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams)
    {
        ValidateConnection(responder);
        ValidateId(sessionId, nameof(sessionId));
        if (!Enum.IsDefined(answer)) throw new ArgumentOutOfRangeException(nameof(answer));
        IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> negotiated = answer == LiveSessionAnswer.Accept
            ? ValidateStreams(streams)
            : ValidateOptionalStreams(streams);
        return TransitionSession(
            responder,
            sessionId,
            session =>
            {
                if (session.State != LiveSessionState.Offered) throw new InvalidOperationException("Only an offered session can be answered.");
                if (session.Remote != responder) throw new UnauthorizedAccessException("Only the remote session participant can answer the offer.");
                if (answer == LiveSessionAnswer.Accept) EnsureNegotiatedStreamsWereOffered(session.OfferedStreams, negotiated);
                return (answer == LiveSessionAnswer.Accept ? LiveSessionState.Accepted : LiveSessionState.Rejected,
                    answer == LiveSessionAnswer.Accept ? negotiated : Array.Empty<LiveStreamDescriptor<TStreamDescriptor>>(),
                    answer == LiveSessionAnswer.Accept ? string.Empty : "Rejected",
                    LiveSessionControlKind.Answer,
                    session.Origin);
            });
    }

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> StartSession(
        DeviceConnection origin,
        Guid sessionId)
        => TransitionSession(
            origin,
            sessionId,
            session =>
            {
                if (session.State != LiveSessionState.Accepted) throw new InvalidOperationException("Only an accepted session can be started.");
                if (session.Origin != origin) throw new UnauthorizedAccessException("Only the session origin can start the session.");
                return (LiveSessionState.Starting, session.NegotiatedStreams, string.Empty, LiveSessionControlKind.Start, session.Remote);
            });

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> ActivateSession(
        DeviceConnection participant,
        Guid sessionId)
        => TransitionSession(
            participant,
            sessionId,
            session =>
            {
                EnsureParticipant(session, participant);
                if (session.State != LiveSessionState.Starting) throw new InvalidOperationException("Only a starting session can become active.");
                return (LiveSessionState.Active, session.NegotiatedStreams, string.Empty, LiveSessionControlKind.Started, OtherParticipant(session, participant));
            });

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> StopSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail)
        => TransitionSession(
            participant,
            sessionId,
            session =>
            {
                EnsureParticipant(session, participant);
                if (session.IsTerminal) throw new InvalidOperationException("A terminal live session cannot be stopped again.");
                return (LiveSessionState.Stopping, session.NegotiatedStreams, NormalizeDetail(detail), LiveSessionControlKind.Stop, OtherParticipant(session, participant));
            });

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> CompleteStopSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail)
        => TransitionSession(
            participant,
            sessionId,
            session =>
            {
                EnsureParticipant(session, participant);
                if (session.State != LiveSessionState.Stopping) throw new InvalidOperationException("Only a stopping session can become stopped.");
                return (LiveSessionState.Stopped, session.NegotiatedStreams, NormalizeDetail(detail), LiveSessionControlKind.Stopped, OtherParticipant(session, participant));
            });

    public ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> FailSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail)
        => TransitionSession(
            participant,
            sessionId,
            session =>
            {
                EnsureParticipant(session, participant);
                if (session.IsTerminal) throw new InvalidOperationException("A terminal live session cannot fail again.");
                return (LiveSessionState.Failed, session.NegotiatedStreams, NormalizeDetail(detail), LiveSessionControlKind.Fail, OtherParticipant(session, participant));
            });

    private ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> TransitionSession(
        DeviceConnection actor,
        Guid sessionId,
        Func<LiveSessionSnapshot<TStreamDescriptor>, (LiveSessionState State, IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> Streams, string Detail, LiveSessionControlKind Kind, DeviceConnection Recipient)> transition)
    {
        ValidateConnection(actor);
        ValidateId(sessionId, nameof(sessionId));
        ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> dispatch;
        lock (gate)
        {
            ThrowIfDisposed();
            ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current = snapshot;
            EnsureCurrentConnectionLocked(actor);
            LiveSessionSnapshot<TStreamDescriptor> existing = GetSession(current, sessionId);
            var result = transition(existing);
            EnsureCurrentConnectionLocked(result.Recipient);
            long revision = current.Revision + 1;
            DateTimeOffset now = timeProvider.GetUtcNow();
            var changedSession = new LiveSessionSnapshot<TStreamDescriptor>(
                existing.SessionId,
                existing.Origin,
                existing.Remote,
                result.State,
                existing.OfferedStreams,
                result.Streams,
                result.Detail,
                revision,
                existing.CreatedAt,
                now);
            ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> changed = CreateSnapshot(
                current,
                revision,
                current.MembershipRevision,
                current.Devices,
                ReplaceSession(current.Sessions, changedSession),
                current.MembershipHistory);
            RetireTerminalSessionId(changedSession);
            CommitLocked(changed, [], []);
            dispatch = CreateDispatch(actor, new LiveSessionControl<TStreamDescriptor>(result.Kind, changedSession), [CreateRoute(GetConnectionLocked(result.Recipient.DeviceId))]);
        }
        PublishStateChanged();
        return dispatch;
    }

    private ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> RemoveConnection(
        DeviceConnection connection,
        DeviceMembershipChangeKind kind)
    {
        ValidateConnection(connection);
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>? changed = null;
        ConnectionEntry? removedEntry = null;
        lock (gate)
        {
            ThrowIfDisposed();
            ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current = snapshot;
            if (!connections.TryGetValue(connection.DeviceId, out ConnectionEntry? entry) ||
                entry.Device.ConnectionId != connection.ConnectionId)
            {
                return current;
            }
            removedEntry = entry;
            connections.Remove(connection.DeviceId);
            long revision = current.Revision + 1;
            long membershipRevision = current.MembershipRevision + 1;
            DateTimeOffset now = timeProvider.GetUtcNow();
            var membershipChange = new DeviceMembershipChange(Guid.NewGuid(), kind, connection.DeviceId, connection.ConnectionId, Guid.Empty, membershipRevision, now);
            IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> sessions = FailSessionsForConnection(current.Sessions, connection, revision, now);
            IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> sessionDispatches =
                CreateFailureDispatchesForConnection(current.Sessions, connection, sessions);
            changed = CreateSnapshot(
                current,
                revision,
                membershipRevision,
                connections.Values.Select(candidate => candidate.Device),
                sessions,
                AppendMembershipHistory(current.MembershipHistory, [membershipChange]));
            foreach (LiveSessionSnapshot<TStreamDescriptor> failed in sessions.Where(session => session.IsTerminal)) RetireTerminalSessionId(failed);
            CommitLocked(changed, [membershipChange], sessionDispatches);
        }
        removedEntry?.Revoke(logger);
        PublishStateChanged();
        return changed;
    }

    private IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> FailSessionsForConnection(
        IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> sessions,
        DeviceConnection connection,
        long revision,
        DateTimeOffset now)
        => HubValidation.Snapshot(sessions.Select(session =>
            !session.IsTerminal && (session.Origin == connection || session.Remote == connection)
                ? new LiveSessionSnapshot<TStreamDescriptor>(
                    session.SessionId,
                    session.Origin,
                    session.Remote,
                    LiveSessionState.Failed,
                    session.OfferedStreams,
                    session.NegotiatedStreams,
                    "A session connection was disconnected.",
                    revision,
                    session.CreatedAt,
                    now)
                : session));

    private IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> ValidateStreams(
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        LiveStreamDescriptor<TStreamDescriptor>[] materialized = streams.ToArray();
        if (materialized.Length == 0) throw new ArgumentException("At least one live stream is required.", nameof(streams));
        if (materialized.Any(stream => stream is null)) throw new ArgumentException("Live streams cannot contain null entries.", nameof(streams));
        if (materialized.Any(stream => stream.StreamId == Guid.Empty)) throw new ArgumentException("Live stream identifiers must be non-empty.", nameof(streams));
        if (materialized.Select(stream => stream.StreamId).Distinct().Count() != materialized.Length) throw new ArgumentException("Live stream identifiers must be unique.", nameof(streams));
        if (materialized.Any(stream => string.IsNullOrWhiteSpace(stream.Kind) || stream.Kind.Trim().Length > options.MaximumStreamKindLength)) throw new ArgumentException("Live stream kinds are invalid.", nameof(streams));
        if (materialized.Any(stream => !Enum.IsDefined(stream.Direction))) throw new ArgumentException("Live stream directions are invalid.", nameof(streams));
        if (materialized.Any(stream => stream.Descriptor is null)) throw new ArgumentException("Live stream descriptors cannot be null.", nameof(streams));
        return Array.AsReadOnly(materialized.Select(stream => stream with { Kind = stream.Kind.Trim() }).ToArray());
    }

    private IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> ValidateOptionalStreams(
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        LiveStreamDescriptor<TStreamDescriptor>[] materialized = streams.ToArray();
        return materialized.Length == 0 ? [] : ValidateStreams(materialized);
    }

    private static void EnsureNegotiatedStreamsWereOffered(
        IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> offered,
        IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> negotiated)
    {
        foreach (LiveStreamDescriptor<TStreamDescriptor> stream in negotiated)
        {
            LiveStreamDescriptor<TStreamDescriptor>? candidate = offered.FirstOrDefault(offeredStream => offeredStream.StreamId == stream.StreamId);
            if (candidate is null ||
                !string.Equals(candidate.Kind, stream.Kind, StringComparison.OrdinalIgnoreCase) ||
                candidate.Direction != stream.Direction)
            {
                throw new InvalidOperationException("Negotiated streams must be a subset of the offered stream identities, kinds, and directions.");
            }
        }
    }

    private IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> CreateFailureDispatchesForConnection(
        IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> previousSessions,
        DeviceConnection connection,
        IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> changedSessions)
    {
        if (connection.DeviceId == Guid.Empty) return [];
        var dispatches = new List<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>>();
        foreach (LiveSessionSnapshot<TStreamDescriptor> previous in previousSessions.Where(session =>
            !session.IsTerminal && (session.Origin == connection || session.Remote == connection)))
        {
            LiveSessionSnapshot<TStreamDescriptor> failed = changedSessions.Single(session => session.SessionId == previous.SessionId);
            DeviceConnection survivor = previous.Origin == connection ? previous.Remote : previous.Origin;
            if (!IsCurrentConnectionLocked(survivor)) continue;
            dispatches.Add(CreateDispatch(
                connection,
                new LiveSessionControl<TStreamDescriptor>(LiveSessionControlKind.Fail, failed),
                [CreateRoute(GetConnectionLocked(survivor.DeviceId))]));
        }
        return Array.AsReadOnly(dispatches.ToArray());
    }

    private ConnectedDeviceDispatch<TMessage, TConnectionRoute> CreateDispatch<TMessage>(
        DeviceConnection sender,
        TMessage message,
        IEnumerable<ConnectedDeviceRoute<TConnectionRoute>> recipients)
        where TMessage : notnull
        => new(
            Guid.NewGuid(),
            snapshot.HubId,
            sender,
            message,
            Array.AsReadOnly(recipients.ToArray()),
            timeProvider.GetUtcNow());

    private ConnectedDeviceRoute<TConnectionRoute> CreateRoute(ConnectionEntry entry)
        => new(
            snapshot.HubId,
            entry.Device.DeviceId,
            entry.Device.ConnectionId,
            snapshot.MembershipRevision,
            entry.Device.Route,
            entry.Revocation.Token);

    private ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> CreateSnapshot(
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current,
        long revision,
        long membershipRevision,
        IEnumerable<ConnectedDevice<TDeviceDescriptor, TConnectionRoute>> devices,
        IEnumerable<LiveSessionSnapshot<TStreamDescriptor>> sessions,
        IEnumerable<DeviceMembershipChange> membershipHistory)
        => new(
            current.HubId,
            revision,
            membershipRevision,
            devices.OrderBy(device => device.DeviceId),
            TrimTerminalSessions(sessions),
            membershipHistory);

    private IReadOnlyList<DeviceMembershipChange> AppendMembershipHistory(
        IReadOnlyList<DeviceMembershipChange> current,
        IEnumerable<DeviceMembershipChange> changes)
        => HubValidation.Snapshot(current.Concat(changes).TakeLast(options.MembershipHistoryCapacity));

    private IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> AppendSession(
        IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> current,
        LiveSessionSnapshot<TStreamDescriptor> session)
        => TrimTerminalSessions(current.Append(session));

    private IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> ReplaceSession(
        IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> current,
        LiveSessionSnapshot<TStreamDescriptor> session)
        => TrimTerminalSessions(current.Select(candidate => candidate.SessionId == session.SessionId ? session : candidate));

    private IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> TrimTerminalSessions(
        IEnumerable<LiveSessionSnapshot<TStreamDescriptor>> sessions)
    {
        LiveSessionSnapshot<TStreamDescriptor>[] materialized = sessions.ToArray();
        HashSet<Guid> retainedTerminalIds = materialized
            .Where(session => session.IsTerminal)
            .OrderByDescending(session => session.UpdatedAt)
            .Take(options.MaximumRetainedTerminalSessions)
            .Select(session => session.SessionId)
            .ToHashSet();
        return HubValidation.Snapshot(materialized.Where(session => !session.IsTerminal || retainedTerminalIds.Contains(session.SessionId)));
    }

    private void RetireTerminalSessionId(LiveSessionSnapshot<TStreamDescriptor> session)
    {
        if (!session.IsTerminal || !retiredSessionIdSet.Add(session.SessionId)) return;
        retiredSessionIds.Enqueue(session.SessionId);
        while (retiredSessionIds.Count > options.MaximumRetiredSessionIds)
        {
            retiredSessionIdSet.Remove(retiredSessionIds.Dequeue());
        }
    }

    private void CommitLocked(
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> changed,
        IReadOnlyList<DeviceMembershipChange> membershipChanges,
        IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> sessionDispatches)
    {
        Volatile.Write(ref snapshot, changed);
        lock (eventGate)
        {
            pendingEvents.Add(changed.Revision, new PendingStateChange(changed, membershipChanges, sessionDispatches));
        }
    }

    private void PublishStateChanged()
    {
        lock (eventGate)
        {
            if (publishingEvents) return;
            publishingEvents = true;
        }
        while (true)
        {
            PendingStateChange? pending;
            lock (eventGate)
            {
                if (!pendingEvents.Remove(publishedRevision + 1, out pending))
                {
                    publishingEvents = false;
                    return;
                }
                publishedRevision = pending.Snapshot.Revision;
            }
            var handlers = StateChanged;
            if (handlers is null) continue;
            var eventArgs = new ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>(pending.Snapshot, pending.MembershipChanges, pending.SessionDispatches);
            foreach (EventHandler<ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>> handler in handlers.GetInvocationList().Cast<EventHandler<ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>>>())
            {
                try
                {
                    handler(this, eventArgs);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "A connected-device hub subscriber failed for hub {HubId} at revision {Revision}.", pending.Snapshot.HubId, pending.Snapshot.Revision);
                }
            }
        }
    }

    private ConnectionEntry GetConnectionLocked(Guid deviceId)
        => connections.TryGetValue(deviceId, out ConnectionEntry? entry)
            ? entry
            : throw new KeyNotFoundException($"Device {deviceId} is not connected.");

    private bool IsCurrentConnectionLocked(DeviceConnection connection)
        => connections.TryGetValue(connection.DeviceId, out ConnectionEntry? entry) &&
            entry.Device.ConnectionId == connection.ConnectionId;

    private void EnsureCurrentConnectionLocked(DeviceConnection connection)
    {
        if (!IsCurrentConnectionLocked(connection)) throw new InvalidOperationException("The device connection is not current.");
    }

    private static LiveSessionSnapshot<TStreamDescriptor> GetSession(
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> current,
        Guid sessionId)
        => current.Sessions.FirstOrDefault(session => session.SessionId == sessionId)
            ?? throw new KeyNotFoundException($"Live session {sessionId} was not found.");

    private static void EnsureParticipant(LiveSessionSnapshot<TStreamDescriptor> session, DeviceConnection participant)
    {
        if (session.Origin != participant && session.Remote != participant) throw new UnauthorizedAccessException("The connection is not a participant in the live session.");
    }

    private static DeviceConnection OtherParticipant(LiveSessionSnapshot<TStreamDescriptor> session, DeviceConnection participant)
        => session.Origin == participant ? session.Remote : session.Origin;

    private string NormalizeDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        string normalized = detail.Trim();
        if (normalized.Length > options.MaximumSessionDetailLength) throw new ArgumentOutOfRangeException(nameof(detail));
        return normalized;
    }

    private static void ValidateConnection(DeviceConnection connection)
    {
        ValidateId(connection.DeviceId, nameof(connection.DeviceId));
        ValidateId(connection.ConnectionId, nameof(connection.ConnectionId));
    }

    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty identifier is required.", parameterName);
    }

    private static void ValidateOptions(ConnectedDeviceHubOptions value)
    {
        if (value.MaximumConnectedDevices <= 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumConnectedDevices));
        if (value.MaximumSessions <= 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumSessions));
        if (value.MaximumRetainedTerminalSessions < 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumRetainedTerminalSessions));
        if (value.MaximumRetiredSessionIds <= 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumRetiredSessionIds));
        if (value.MembershipHistoryCapacity < 0) throw new ArgumentOutOfRangeException(nameof(value.MembershipHistoryCapacity));
        if (value.MaximumStreamKindLength <= 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumStreamKindLength));
        if (value.MaximumSessionDetailLength <= 0) throw new ArgumentOutOfRangeException(nameof(value.MaximumSessionDetailLength));
    }

    private sealed class ConnectionEntry(ConnectedDevice<TDeviceDescriptor, TConnectionRoute> device)
    {
        public ConnectedDevice<TDeviceDescriptor, TConnectionRoute> Device { get; } = device;

        public CancellationTokenSource Revocation { get; } = new();

        public void Revoke(ILogger logger)
        {
            try
            {
                Revocation.Cancel();
            }
            catch (AggregateException exception)
            {
                logger.LogWarning(exception, "A connected-device route revocation callback failed.");
            }
            finally
            {
                Revocation.Dispose();
            }
        }
    }

    private sealed record PendingStateChange(
        ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Snapshot,
        IReadOnlyList<DeviceMembershipChange> MembershipChanges,
        IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> SessionDispatches);

    public void Dispose()
    {
        ConnectionEntry[] entries;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            entries = connections.Values.ToArray();
            connections.Clear();
        }
        foreach (ConnectionEntry entry in entries) entry.Revoke(logger);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
