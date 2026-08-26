namespace TripleG3.P2P.Hubs;

public sealed class HubOptions
{
    public int MaximumMembers { get; init; } = 256;

    public int MaximumUsernameLength { get; init; } = 64;

    public int MaximumMessageLength { get; init; } = 4_096;

    public int MaximumTeamNameLength { get; init; } = 64;

    public int MessageHistoryCapacity { get; init; } = 200;

    public int NotificationHistoryCapacity { get; init; } = 200;
}