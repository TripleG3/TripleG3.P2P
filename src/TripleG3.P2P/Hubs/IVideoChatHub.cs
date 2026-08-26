namespace TripleG3.P2P.Hubs;

public interface IVideoChatHub
{
    VideoChatHubSnapshot Snapshot { get; }

    event EventHandler<HubStateChangedEventArgs<VideoChatHubSnapshot>>? StateChanged;

    VideoChatHubSnapshot Join(Guid memberId, string username);

    VideoChatHubSnapshot Leave(Guid memberId);

    VideoChatHubSnapshot SetCameraEnabled(Guid memberId, bool enabled);

    VideoChatHubSnapshot SetMicrophoneEnabled(Guid memberId, bool enabled);

    VideoChatDispatch<HubChatMessage> SendMessage(Guid senderMemberId, string text);

    VideoChatDispatch<TMessage> RouteMessage<TMessage>(Guid senderMemberId, TMessage message);

    VideoChatRecipientRoute GetMediaRoute(Guid senderMemberId, VideoChatMediaKind mediaKind);

    bool IsRouteCurrent(VideoChatRecipientRoute route);
}