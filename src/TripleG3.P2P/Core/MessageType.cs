namespace TripleG3.P2P.Core;

/// <summary>
/// Logical classification of a transport message; reserved for future expansion (e.g. control / ack / error).
/// </summary>
public enum MessageType : short
{
    /// <summary>
    /// Undefined / placeholder.
    /// </summary>
    None = 0,
    /// <summary>
    /// Application data payload.
    /// </summary>
    Data = 1
}
