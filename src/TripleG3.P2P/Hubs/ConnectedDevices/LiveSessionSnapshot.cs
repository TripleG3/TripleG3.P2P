namespace TripleG3.P2P.Hubs;

public sealed record LiveSessionSnapshot<TStreamDescriptor>
{
    public LiveSessionSnapshot(
        Guid sessionId,
        DeviceConnection origin,
        DeviceConnection remote,
        LiveSessionState state,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> offeredStreams,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> negotiatedStreams,
        string detail,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        SessionId = sessionId;
        Origin = origin;
        Remote = remote;
        State = state;
        OfferedStreams = Array.AsReadOnly(offeredStreams.ToArray());
        NegotiatedStreams = Array.AsReadOnly(negotiatedStreams.ToArray());
        Detail = detail;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid SessionId { get; }

    public DeviceConnection Origin { get; }

    public DeviceConnection Remote { get; }

    public LiveSessionState State { get; }

    public IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> OfferedStreams { get; }

    public IReadOnlyList<LiveStreamDescriptor<TStreamDescriptor>> NegotiatedStreams { get; }

    public string Detail { get; }

    public long Revision { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public bool IsTerminal => State is LiveSessionState.Rejected or LiveSessionState.Stopped or LiveSessionState.Failed;
}
