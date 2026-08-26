namespace TripleG3.P2P.Hubs;

public sealed class NotificationsHubOptions
{
    public int MaximumDevices { get; init; } = 10_000;

    public int MaximumTitleLength { get; init; } = 256;

    public int MaximumBodyLength { get; init; } = 4_096;

    public int MaximumDataEntries { get; init; } = 64;

    public int MaximumActions { get; init; } = 8;

    public int MaximumDataKeyLength { get; init; } = 128;

    public int MaximumDataValueLength { get; init; } = 4_096;

    public int MaximumActionTitleLength { get; init; } = 128;
}