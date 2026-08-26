namespace TripleG3.P2P.Hubs;

public interface IOwnerlessChatHub : IChatHub
{
    ChatHubSnapshot Join(Guid memberId, string username);

    ChatHubSnapshot Leave(Guid memberId);
}