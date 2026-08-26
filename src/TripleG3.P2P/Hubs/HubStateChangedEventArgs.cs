namespace TripleG3.P2P.Hubs;

public sealed class HubStateChangedEventArgs<TSnapshot>(TSnapshot snapshot) : EventArgs
{
    public TSnapshot Snapshot { get; } = snapshot;
}