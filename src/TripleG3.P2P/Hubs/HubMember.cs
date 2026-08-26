namespace TripleG3.P2P.Hubs;

public sealed record HubMember(
    Guid MemberId,
    string Username,
    HubMemberRole Role,
    DateTimeOffset JoinedAt);