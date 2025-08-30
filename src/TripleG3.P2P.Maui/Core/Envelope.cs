using TripleG3.P2P.Maui.Attributes;

namespace TripleG3.P2P.Maui.Core;

/// <summary>
/// Generic transport envelope carrying a protocol <see cref="TypeName"/> plus the typed <see cref="Message"/> payload.
/// Serialized differently depending on the <see cref="SerializationProtocol"/>.
/// </summary>
/// <typeparam name="T">Payload contract type.</typeparam>
public record Envelope<T>([property: Udp(1)] string TypeName, T? Message)
{
    /// <summary>
    /// An empty envelope instance (TypeName = empty, Message = default).
    /// </summary>
    public static Envelope<T> Empty { get; } = new(string.Empty, default);
}
