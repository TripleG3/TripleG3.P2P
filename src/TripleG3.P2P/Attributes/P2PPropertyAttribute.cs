namespace TripleG3.P2P.Attributes;

/// <summary>
/// Marks a class, struct, or property as part of a P2P attribute serialization contract.
/// On properties the optional order parameter defines the serialization order.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false)]
public sealed class P2PPropertyAttribute : Attribute
{
    /// <summary>
    /// Serialization order for the property (lower first). Null means no explicit ordering (max value).
    /// </summary>
    public int? Order { get; }
    public P2PPropertyAttribute()
    {
    }

    public P2PPropertyAttribute(int order) => Order = order;
}
