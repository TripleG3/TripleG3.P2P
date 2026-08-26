namespace TripleG3.P2P.Hubs;

public interface IChatHub
{
    ChatHubSnapshot Snapshot { get; }

    event EventHandler<HubStateChangedEventArgs<ChatHubSnapshot>>? StateChanged;

    HubDispatch SendMessage(Guid senderMemberId, string text);
}