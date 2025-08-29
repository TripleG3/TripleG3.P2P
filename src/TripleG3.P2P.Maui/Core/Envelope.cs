using TripleG3.P2P.Maui.Attributes;

namespace TripleG3.P2P.Maui.Core;

public record Envelope<T>([property: Udp(1)] string TypeName, T? Message)
{
    public static Envelope<T> Empty { get; } = new(string.Empty, default);
}
