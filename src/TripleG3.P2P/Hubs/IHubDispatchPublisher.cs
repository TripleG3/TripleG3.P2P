namespace TripleG3.P2P.Hubs;

public interface IHubDispatchPublisher
{
    ValueTask PublishAsync(HubDispatch dispatch, CancellationToken cancellationToken = default);

    ValueTask PublishAsync<TMessage>(HubDispatch<TMessage> dispatch, CancellationToken cancellationToken = default);
}