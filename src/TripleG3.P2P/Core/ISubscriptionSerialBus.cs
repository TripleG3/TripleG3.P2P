namespace TripleG3.P2P.Core;

/// <summary>
/// Optional serial-bus capability for subscriptions that can be removed independently of the bus lifetime.
/// </summary>
public interface ISubscriptionSerialBus : ISerialBus
{
    /// <summary>
    /// Subscribes a handler and returns a registration that removes it when disposed.
    /// </summary>
    IDisposable Subscribe<T>(Action<T> handler);
}