namespace TripleG3.P2P.Hubs;

public sealed class ConnectedDeviceHubOptions
{
    public int MaximumConnectedDevices { get; init; } = 256;

    public int MaximumSessions { get; init; } = 256;

    public int MaximumRetainedTerminalSessions { get; init; } = 256;

    public int MaximumRetiredSessionIds { get; init; } = 4_096;

    public int MembershipHistoryCapacity { get; init; } = 200;

    public int MaximumStreamKindLength { get; init; } = 128;

    public int MaximumSessionDetailLength { get; init; } = 1_024;
}
