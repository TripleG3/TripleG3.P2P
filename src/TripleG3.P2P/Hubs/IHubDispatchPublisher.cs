namespace TripleG3.P2P.Hubs;

public interface IHubDispatchPublisher
{
    ValueTask PublishAsync(HubDispatch dispatch, CancellationToken cancellationToken = default);
}