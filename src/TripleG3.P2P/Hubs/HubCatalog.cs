using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripleG3.P2P.Hubs;

public sealed class HubCatalog : IHubCatalog
{
    private readonly ConcurrentDictionary<Guid, ChatHub> _chatHubs = new();
    private readonly ConcurrentDictionary<Guid, HostedChatHub> _hostedChatHubs = new();
    private readonly ConcurrentDictionary<Guid, GamingLobbyHub> _gamingLobbies = new();
    private readonly ConcurrentDictionary<Guid, NotificationsHub> _notificationsHubs = new();
    private readonly ConcurrentDictionary<Guid, VideoChatHub> _videoChatHubs = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public HubCatalog(TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IReadOnlyCollection<Guid> ChatHubIds => Array.AsReadOnly(_chatHubs.Keys.Order().ToArray());

    public IReadOnlyCollection<Guid> HostedChatHubIds => Array.AsReadOnly(_hostedChatHubs.Keys.Order().ToArray());

    public IReadOnlyCollection<Guid> GamingLobbyIds => Array.AsReadOnly(_gamingLobbies.Keys.Order().ToArray());

    public IReadOnlyCollection<Guid> NotificationsHubIds => Array.AsReadOnly(_notificationsHubs.Keys.Order().ToArray());

    public IReadOnlyCollection<Guid> VideoChatHubIds => Array.AsReadOnly(_videoChatHubs.Keys.Order().ToArray());

    public ChatHub CreateChatHub(Guid hubId, HubOptions? options = null)
    {
        var hub = new ChatHub(hubId, options, _timeProvider, _loggerFactory.CreateLogger<ChatHub>());
        if (!_chatHubs.TryAdd(hubId, hub)) throw new InvalidOperationException($"Chat hub {hubId} already exists.");
        return hub;
    }

    public HostedChatHub CreateHostedChatHub(
        Guid hubId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null)
    {
        var hub = new HostedChatHub(
            hubId,
            initialHostMemberId,
            initialHostUsername,
            options,
            _timeProvider,
            _loggerFactory.CreateLogger<HostedChatHub>());
        if (!_hostedChatHubs.TryAdd(hubId, hub)) throw new InvalidOperationException($"Hosted chat hub {hubId} already exists.");
        return hub;
    }

    public GamingLobbyHub CreateGamingLobby(
        Guid lobbyId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null)
    {
        var hub = new GamingLobbyHub(
            lobbyId,
            initialHostMemberId,
            initialHostUsername,
            options,
            _timeProvider,
            _loggerFactory.CreateLogger<GamingLobbyHub>());
        if (!_gamingLobbies.TryAdd(lobbyId, hub)) throw new InvalidOperationException($"Gaming lobby {lobbyId} already exists.");
        return hub;
    }

    public NotificationsHub CreateNotificationsHub(Guid hubId, NotificationsHubOptions? options = null)
    {
        var hub = new NotificationsHub(
            hubId,
            options,
            _timeProvider,
            new DefaultNotificationProjector(),
            _loggerFactory.CreateLogger<NotificationsHub>());
        if (!_notificationsHubs.TryAdd(hubId, hub)) throw new InvalidOperationException($"Notifications hub {hubId} already exists.");
        return hub;
    }

    public VideoChatHub CreateVideoChatHub(Guid hubId, HubOptions? options = null)
    {
        var hub = new VideoChatHub(hubId, options, _timeProvider, _loggerFactory.CreateLogger<VideoChatHub>());
        if (!_videoChatHubs.TryAdd(hubId, hub)) throw new InvalidOperationException($"Video chat hub {hubId} already exists.");
        return hub;
    }

    public bool TryGetChatHub(Guid hubId, out ChatHub? hub) => _chatHubs.TryGetValue(hubId, out hub);

    public bool TryGetHostedChatHub(Guid hubId, out HostedChatHub? hub) => _hostedChatHubs.TryGetValue(hubId, out hub);

    public bool TryGetGamingLobby(Guid lobbyId, out GamingLobbyHub? hub) => _gamingLobbies.TryGetValue(lobbyId, out hub);

    public bool TryGetNotificationsHub(Guid hubId, out NotificationsHub? hub) => _notificationsHubs.TryGetValue(hubId, out hub);

    public bool TryGetVideoChatHub(Guid hubId, out VideoChatHub? hub) => _videoChatHubs.TryGetValue(hubId, out hub);

    public bool RemoveChatHub(Guid hubId, ChatHub expectedHub)
        => ((ICollection<KeyValuePair<Guid, ChatHub>>)_chatHubs).Remove(new KeyValuePair<Guid, ChatHub>(hubId, expectedHub));

    public bool RemoveHostedChatHub(Guid hubId, HostedChatHub expectedHub)
        => ((ICollection<KeyValuePair<Guid, HostedChatHub>>)_hostedChatHubs).Remove(new KeyValuePair<Guid, HostedChatHub>(hubId, expectedHub));

    public bool RemoveGamingLobby(Guid lobbyId, GamingLobbyHub expectedLobby)
        => ((ICollection<KeyValuePair<Guid, GamingLobbyHub>>)_gamingLobbies).Remove(new KeyValuePair<Guid, GamingLobbyHub>(lobbyId, expectedLobby));

    public bool RemoveNotificationsHub(Guid hubId, NotificationsHub expectedHub)
        => ((ICollection<KeyValuePair<Guid, NotificationsHub>>)_notificationsHubs).Remove(new KeyValuePair<Guid, NotificationsHub>(hubId, expectedHub));

    public bool RemoveVideoChatHub(Guid hubId, VideoChatHub expectedHub)
        => ((ICollection<KeyValuePair<Guid, VideoChatHub>>)_videoChatHubs).Remove(new KeyValuePair<Guid, VideoChatHub>(hubId, expectedHub));
}