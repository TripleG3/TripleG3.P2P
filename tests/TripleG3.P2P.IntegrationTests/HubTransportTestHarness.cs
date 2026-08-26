using System.Collections.Concurrent;
using System.Net;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;

namespace TripleG3.P2P.IntegrationTests;

internal sealed class HubTransportTestHarness : IAsyncDisposable
{
    private static int _portSeed = 20000;
    private readonly Func<ISerialBus> _createBus;
    private readonly Dictionary<Guid, HubTestMemberSession> _sessions = [];
    private readonly List<ISerialBus> _publisherBuses = [];

    public HubTransportTestHarness(Func<ISerialBus> createBus)
    {
        ArgumentNullException.ThrowIfNull(createBus);
        _createBus = createBus;
    }

    public async Task<HubTestMemberSession> AddMemberAsync(Guid memberId)
    {
        var session = new HubTestMemberSession(memberId, _createBus(), NextPort());
        await session.StartAsync();
        if (!_sessions.TryAdd(memberId, session))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException($"Member session {memberId} already exists.");
        }

        return session;
    }

    public async ValueTask PublishAsync(HubDispatch dispatch, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(dispatch.RecipientMemberIds, dispatch.Message, cancellationToken);

    public async ValueTask PublishAsync<TMessage>(HubDispatch<TMessage> dispatch, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(dispatch.RecipientMemberIds, dispatch.Message, cancellationToken);

    public async ValueTask PublishAsync<TMessage>(VideoChatDispatch<TMessage> dispatch, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(dispatch.RecipientMemberIds, dispatch.Message, cancellationToken);

    private async ValueTask PublishCoreAsync<TMessage>(
        IReadOnlyList<Guid> recipientMemberIds,
        TMessage message,
        CancellationToken cancellationToken)
    {
        foreach (var recipientMemberId in recipientMemberIds)
        {
            if (!_sessions.TryGetValue(recipientMemberId, out var session))
            {
                throw new KeyNotFoundException($"No transport session exists for hub member {recipientMemberId}.");
            }

            var publisher = _createBus();
            _publisherBuses.Add(publisher);
            await publisher.StartListeningAsync(new ProtocolConfiguration
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = 0,
                OutboundEndPoints = [session.EndPoint],
                SerializationProtocol = SerializationProtocol.LengthPrefixed
            }, cancellationToken);
            await publisher.SendAsync(message, cancellationToken: cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var publisher in _publisherBuses)
        {
            await publisher.CloseConnectionAsync();
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }

    private static int NextPort()
    {
        var port = Interlocked.Increment(ref _portSeed);
        if (port <= 60000) return port;
        Interlocked.Exchange(ref _portSeed, 20000);
        return Interlocked.Increment(ref _portSeed);
    }
}

internal sealed class HubTestMemberSession : IAsyncDisposable
{
    private readonly ISerialBus _bus;
    private readonly ConcurrentQueue<HubChatMessage> _messages = new();

    public HubTestMemberSession(Guid memberId, ISerialBus bus, int port)
    {
        MemberId = memberId;
        _bus = bus;
        EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        _bus.SubscribeTo<HubChatMessage>(_messages.Enqueue);
    }

    public Guid MemberId { get; }

    public IPEndPoint EndPoint { get; }

    public IReadOnlyCollection<HubChatMessage> Messages => _messages.ToArray();

    public ConcurrentQueue<TMessage> Subscribe<TMessage>()
    {
        var messages = new ConcurrentQueue<TMessage>();
        _bus.SubscribeTo<TMessage>(messages.Enqueue);
        return messages;
    }

    public Task WaitForMessageCountAsync(int expectedCount, int timeoutMilliseconds = 5000)
        => WaitForAsync(() => _messages.Count >= expectedCount, timeoutMilliseconds);

    public async Task StartAsync()
    {
        await _bus.StartListeningAsync(new ProtocolConfiguration
        {
            LocalAddress = IPAddress.Loopback,
            LocalPort = EndPoint.Port,
            SerializationProtocol = SerializationProtocol.LengthPrefixed
        });
    }

    public async ValueTask DisposeAsync() => await _bus.CloseConnectionAsync();

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Expected hub transport delivery did not complete before timeout.");
    }
}