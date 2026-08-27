namespace TripleG3.P2P.Hubs;

public enum LiveSessionState
{
    Offered = 0,
    Accepted,
    Rejected,
    Starting,
    Active,
    Stopping,
    Stopped,
    Failed
}
