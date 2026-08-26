namespace TripleG3.P2P.Attributes;

/// <summary>
/// Identifies a P2P serial-bus message contract and provides the protocol-visible name used during transport.
/// If no name is supplied, the CLR <see cref="Type.Name"/> is used. The name must be consistent between peers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public class P2PMessageAttribute : Attribute
{
    /// <summary>
    /// Protocol type name override. A <see langword="null"/> value uses the CLR type name.
    /// </summary>
    public string? Name { get; }

    public P2PMessageAttribute()
    {
    }

    public P2PMessageAttribute(string name) => Name = name;
}

/// <summary>
/// Generic helper variant that uses <c>typeof(T).Name</c> when applied without an explicit name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class P2PMessageAttribute<T> : P2PMessageAttribute
{
    public P2PMessageAttribute()
        : base(typeof(T).Name)
    {
    }

    public P2PMessageAttribute(string name)
        : base(name)
    {
    }
}