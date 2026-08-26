namespace TripleG3.P2P.Hubs;

public sealed record VideoChatDispatch<TMessage>(
    HubDispatchMetadata Metadata,
    TMessage Message,
    VideoChatRecipientRoute Route)
{
    public Guid HubId => Metadata.HubId;

    public long Revision => Metadata.Revision;

    public Guid SenderMemberId => Metadata.SenderMemberId;

    public DateTimeOffset CreatedAt => Metadata.CreatedAt;

    public IReadOnlyList<Guid> RecipientMemberIds => Route.RecipientMemberIds;
}