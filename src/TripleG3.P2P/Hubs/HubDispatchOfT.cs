namespace TripleG3.P2P.Hubs;

public sealed record HubDispatch<TMessage>(
    HubDispatchMetadata Metadata,
    TMessage Message,
    IReadOnlyList<Guid> RecipientMemberIds)
{
    public Guid HubId => Metadata.HubId;

    public long Revision => Metadata.Revision;

    public Guid SenderMemberId => Metadata.SenderMemberId;

    public HubAudience Audience => Metadata.Audience;

    public Guid TeamId => Metadata.TeamId;

    public DateTimeOffset CreatedAt => Metadata.CreatedAt;
}