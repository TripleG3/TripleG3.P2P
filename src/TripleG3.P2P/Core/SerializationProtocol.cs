namespace TripleG3.P2P.Core;

/// <summary>
/// Supported serialization strategies for converting envelopes into bytes.
/// </summary>
public enum SerializationProtocol : short
{
    /// <summary>
    /// Attribute-driven delimited serialization using ordered <c>[Udp]</c> properties.
    /// </summary>
    None = 0,
    /// <summary>
    /// JSON (System.Text.Json) serialization of the envelope.
    /// </summary>
    JsonRaw = 1
}
