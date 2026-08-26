namespace TripleG3.P2P.Hubs;

public interface IHostedChatHub : IChatHub
{
    ChatHubSnapshot AddMember(Guid requesterMemberId, Guid memberId, string username);

    ChatHubSnapshot RemoveMember(Guid requesterMemberId, Guid memberId);

    ChatHubSnapshot PromoteMember(Guid requesterMemberId, Guid memberId);

    ChatHubSnapshot DemoteMember(Guid requesterMemberId, Guid memberId);

    ChatHubSnapshot Leave(Guid memberId);
}