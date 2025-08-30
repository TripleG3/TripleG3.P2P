namespace TripleG3.P2P.Maui.Attributes;

/// <summary>
/// Identifies a UDP message contract type and provides the protocol-visible name used during transport.
/// If no name supplied, CLR <see cref="Type.Name"/> is used. Must be consistent between peers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public class UdpMessageAttribute : Attribute
{
    /// <summary>
    /// Protocol type name override (null = use CLR name).
    /// </summary>
    public string? Name { get; }
    public UdpMessageAttribute() {}
    public UdpMessageAttribute(string name) => Name = name;
}

/// <summary>
/// Generic helper variant: when applied without an explicit name uses <c>typeof(T).Name</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UdpMessageAttribute<T> : UdpMessageAttribute
{
    public UdpMessageAttribute() : base(typeof(T).Name) {}
    public UdpMessageAttribute(string name) : base(name) {}
}
