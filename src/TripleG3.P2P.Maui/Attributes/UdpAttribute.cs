using System;

namespace TripleG3.P2P.Maui.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false)]
public sealed class UdpAttribute : Attribute
{
    public int? Order { get; }
    public UdpAttribute() {}
    public UdpAttribute(int order) => Order = order;
}
