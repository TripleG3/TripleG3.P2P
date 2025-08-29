namespace TripleG3.P2P.Maui.Attributes;

/// <summary>
/// Identifies a UDP message type and/or provides an override name used for the Envelope TypeName.
/// If no name is provided the runtime <see cref="Type.Name"/> will be used. A generic version is supplied
/// so that an alternate CLR type name can still map to a friendly protocol name consistently on both ends.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public class UdpMessageAttribute : Attribute
{
    public string? Name { get; }
    public UdpMessageAttribute() {}
    public UdpMessageAttribute(string name) => Name = name;
}

/// <summary>
/// Generic helper variant. When applied without a name it uses typeof(T).Name as the protocol name.
/// </summary>
/// <typeparam name="T">The CLR type whose name should be used if no explicit name is supplied.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UdpMessageAttribute<T> : UdpMessageAttribute
{
    public UdpMessageAttribute() : base(typeof(T).Name) {}
    public UdpMessageAttribute(string name) : base(name) {}
}
