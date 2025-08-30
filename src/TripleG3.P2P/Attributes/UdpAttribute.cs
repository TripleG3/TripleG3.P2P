using System;

namespace TripleG3.P2P.Attributes;

/// <summary>
/// Marks a class/struct or property as part of the UDP delimited serialization contract.
/// On properties the optional order parameter defines the serialization order.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false)]
public sealed class UdpAttribute : Attribute
{
    /// <summary>
    /// Serialization order for the property (lower first). Null means no explicit ordering (max value).
    /// </summary>
    public int? Order { get; }
    public UdpAttribute() {}
    public UdpAttribute(int order) => Order = order;
}
