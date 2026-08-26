namespace TripleG3.P2P.Hubs;

public sealed record VideoChatMember(
    Guid MemberId,
    string Username,
    bool IsCameraEnabled,
    bool IsMicrophoneEnabled,
    DateTimeOffset JoinedAt);