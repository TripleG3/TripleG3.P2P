using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Serialization;

namespace TripleG3.P2P.Tcp;

/// <summary>
/// TCP serial bus. Accepted sockets are receive-only sessions; outbound data is sent only to configured endpoints.
/// </summary>
public sealed class TcpSerialBus : ISubscriptionSerialBus, IDisposable, IAsyncDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly IReadOnlyDictionary<SerializationProtocol, IMessageSerializer> _serializers;
    private readonly ILogger<TcpSerialBus> _logger;
    private readonly ConcurrentDictionary<string, (Guid Id, Type Type, Delegate Handler)[]> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TcpConnection> _outboundConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TcpConnection> _inboundConnections = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly object _receiveTasksGate = new();
    private readonly List<Task> _receiveTasks = [];

    private ProtocolConfiguration? _config;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private long _inboundId;
    private int _disposed;

    public TcpSerialBus(IEnumerable<IMessageSerializer> serializers)
        : this(serializers, NullLogger<TcpSerialBus>.Instance)
    {
    }

    public TcpSerialBus(IEnumerable<IMessageSerializer> serializers, ILogger<TcpSerialBus> logger)
    {
        ArgumentNullException.ThrowIfNull(serializers);
        ArgumentNullException.ThrowIfNull(logger);
        _serializers = serializers.ToDictionary(serializer => serializer.Protocol, serializer => serializer);
        _logger = logger;
    }

    public bool IsListening => Volatile.Read(ref _listener) is not null;

    public ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration(config);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_listener is not null) return ValueTask.CompletedTask;

            _config = config;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(config.LocalAddress, config.LocalPort);
            _listener.Start();
            _acceptTask = AcceptLoopAsync(_listener, _cts.Token);
        }

        return ValueTask.CompletedTask;
    }

    public void SubscribeTo<T>(Action<T> handler) => _ = Subscribe(handler);

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var protocolName = GetProtocolTypeName(typeof(T));
        var id = Guid.NewGuid();
        var entry = (Id: id, Type: typeof(T), Handler: (Delegate)handler);
        _subscriptions.AddOrUpdate(protocolName, [entry], (_, current) => [.. current, entry]);

        return new SubscriptionRegistration(() =>
            _subscriptions.AddOrUpdate(
                protocolName,
                [],
                (_, current) => [.. current.Where(item => item.Id != id)]));
    }

    public async ValueTask SendAsync<T>(
        T message,
        MessageType messageType = MessageType.Data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _config ?? throw new InvalidOperationException("Configuration not set. Call StartListeningAsync first.");
        if (!_serializers.TryGetValue(config.SerializationProtocol, out var serializer))
        {
            throw new InvalidOperationException($"Serializer {config.SerializationProtocol} is not registered.");
        }

        if (!Enum.IsDefined(messageType))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        var connectionFailures = await EnsureConnectionsAsync(config, cancellationToken).ConfigureAwait(false);
        var body = serializer.Serialize(new Envelope<T>(GetProtocolTypeName(typeof(T)), message));
        if (body.Length > config.MaxPayloadBytes)
        {
            throw new InvalidDataException($"Serialized payload exceeds the {config.MaxPayloadBytes}-byte configured limit.");
        }

        var frame = new byte[UdpHeader.Size + body.Length];
        new UdpHeader(body.Length, (short)messageType, config.SerializationProtocol).Write(frame);
        body.CopyTo(frame.AsSpan(UdpHeader.Size));

        var failures = new List<Exception>(connectionFailures);
        var successCount = 0;
        var targets = _outboundConnections.ToArray();
        foreach (var target in targets)
        {
            try
            {
                await target.Value.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                successCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
            {
                failures.Add(exception);
                RemoveOutbound(target.Key, target.Value);
                _logger.LogWarning(exception, "TCP send to {RemoteEndPoint} failed.", target.Key);
            }
        }

        if (successCount == 0)
        {
            throw new AggregateException("TCP send failed for every configured endpoint.", failures);
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "TCP broadcast completed partially: {Succeeded} of {Attempted} endpoints succeeded.",
                successCount,
                GetConfiguredEndpoints(config).Count);
        }
    }

    public async ValueTask CloseConnectionAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;

        lock (_lifecycleGate)
        {
            listener = _listener;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
            _config = null;
        }

        if (listener is null) return;
        cts?.Cancel();
        listener.Stop();

        foreach (var outbound in _outboundConnections.ToArray()) RemoveOutbound(outbound.Key, outbound.Value);
        foreach (var inbound in _inboundConnections.ToArray()) RemoveInbound(inbound.Key, inbound.Value);

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] receiveTasks;
        lock (_receiveTasksGate)
        {
            receiveTasks = [.. _receiveTasks];
            _receiveTasks.Clear();
        }

        if (receiveTasks.Length > 0)
        {
            await Task.WhenAll(receiveTasks).ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancellationTokenSource? cts;
        TcpListener? listener;
        lock (_lifecycleGate)
        {
            cts = _cts;
            listener = _listener;
            _cts = null;
            _listener = null;
            _acceptTask = null;
            _config = null;
        }

        cts?.Cancel();
        listener?.Stop();
        foreach (var outbound in _outboundConnections.ToArray()) RemoveOutbound(outbound.Key, outbound.Value);
        foreach (var inbound in _inboundConnections.ToArray()) RemoveInbound(inbound.Key, inbound.Value);
        cts?.Dispose();
        _connectGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseConnectionAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                var config = _config;
                if (config is null || _inboundConnections.Count >= config.MaxInboundConnections)
                {
                    _logger.LogWarning("Rejected TCP connection because the inbound session limit was reached.");
                    client.Dispose();
                    continue;
                }

                var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                var id = Interlocked.Increment(ref _inboundId);
                var connection = new TcpConnection($"inbound:{id}:{remoteEndPoint}", client);
                if (!_inboundConnections.TryAdd(id, connection))
                {
                    connection.Dispose();
                    continue;
                }

                TrackReceiveTask(ReceiveLoopAsync(connection, cancellationToken, () => RemoveInbound(id, connection)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                client?.Dispose();
                _logger.LogWarning(exception, "TCP accept failed.");
            }
        }
    }

    private async Task<IReadOnlyCollection<Exception>> EnsureConnectionsAsync(
        ProtocolConfiguration config,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var endpoint in GetConfiguredEndpoints(config))
            {
                var key = endpoint.ToString();
                if (_outboundConnections.TryGetValue(key, out var existing) && !existing.IsDisposed) continue;
                if (existing is not null) RemoveOutbound(key, existing);

                var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
                try
                {
                    await client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
                    var connection = new TcpConnection(key, client);
                    if (_outboundConnections.TryAdd(key, connection))
                    {
                        TrackReceiveTask(ReceiveLoopAsync(connection, cancellationToken, () => RemoveOutbound(key, connection)));
                    }
                    else
                    {
                        connection.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    client.Dispose();
                    throw;
                }
                catch (Exception exception) when (exception is SocketException or IOException)
                {
                    client.Dispose();
                    var failure = new IOException($"Could not connect to configured TCP endpoint {endpoint}.", exception);
                    failures.Add(failure);
                    _logger.LogWarning(exception, "TCP connection to {RemoteEndPoint} failed.", endpoint);
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }

        return failures;
    }

    private async Task ReceiveLoopAsync(TcpConnection connection, CancellationToken cancellationToken, Action removeConnection)
    {
        var headerBuffer = new byte[UdpHeader.Size];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(connection.Stream, headerBuffer, cancellationToken).ConfigureAwait(false)) break;
                var header = UdpHeader.Read(headerBuffer);
                var config = _config;
                if (config is null) break;
                if (header.Length <= 0 || header.Length > config.MaxPayloadBytes)
                {
                    _logger.LogWarning("Rejected TCP frame from {RemoteEndPoint} with payload length {PayloadLength}.", connection.Key, header.Length);
                    break;
                }

                if (!Enum.IsDefined((MessageType)header.MessageType))
                {
                    _logger.LogWarning("Rejected TCP frame from {RemoteEndPoint} with unknown message type {MessageType}.", connection.Key, header.MessageType);
                    break;
                }

                if (!_serializers.ContainsKey(header.SerializationProtocol))
                {
                    _logger.LogWarning("Rejected TCP frame from {RemoteEndPoint} with unknown protocol {Protocol}.", connection.Key, (short)header.SerializationProtocol);
                    break;
                }

                var payload = new byte[header.Length];
                if (!await ReadExactAsync(connection.Stream, payload, cancellationToken).ConfigureAwait(false)) break;
                ProcessIncoming(payload, header.SerializationProtocol);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || connection.IsDisposed)
        {
        }
        catch (Exception exception) when (exception is SocketException or IOException or JsonException or DecoderFallbackException)
        {
            _logger.LogWarning(exception, "TCP receive loop for {RemoteEndPoint} stopped.", connection.Key);
        }
        finally
        {
            removeConnection();
        }
    }

    private void ProcessIncoming(byte[] payload, SerializationProtocol protocol)
    {
        if (!_serializers.TryGetValue(protocol, out var serializer)) return;
        var protocolName = TryExtractTypeName(payload, protocol);
        if (string.IsNullOrEmpty(protocolName) || !_subscriptions.TryGetValue(protocolName, out var entries)) return;

        foreach (var entry in entries)
        {
            try
            {
                var envelopeType = typeof(Envelope<>).MakeGenericType(entry.Type);
                var envelope = serializer.Deserialize(envelopeType, payload);
                var message = envelopeType.GetProperty(nameof(Envelope<object>.Message))?.GetValue(envelope);
                entry.Handler.DynamicInvoke(message);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "TCP subscriber for {MessageType} failed.", entry.Type);
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static string? TryExtractTypeName(ReadOnlySpan<byte> payload, SerializationProtocol protocol)
    {
        try
        {
            if (protocol == SerializationProtocol.None)
            {
                var delimiter = "@-@"u8;
                var index = payload.IndexOf(delimiter);
                return StrictUtf8.GetString(index < 0 ? payload : payload[..index]);
            }

            if (protocol == SerializationProtocol.JsonRaw)
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;
                if (root.TryGetProperty("typeName", out var camelCase)) return camelCase.GetString();
                if (root.TryGetProperty("TypeName", out var pascalCase)) return pascalCase.GetString();
            }

            if (protocol == SerializationProtocol.LengthPrefixed)
            {
                return LengthPrefixedMessageSerializer.TryExtractTypeName(payload);
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
        }

        return null;
    }

    private static string GetProtocolTypeName(Type type)
    {
        var attribute = type.GetCustomAttribute<UdpMessageAttribute>();
        return attribute?.Name is { Length: > 0 } name ? name : type.Name;
    }

    private static IReadOnlyList<IPEndPoint> GetConfiguredEndpoints(ProtocolConfiguration config)
        => [.. new[] { config.RemoteEndPoint }
            .Concat(config.BroadcastEndPoints)
            .DistinctBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)];

    private void TrackReceiveTask(Task task)
    {
        lock (_receiveTasksGate)
        {
            _receiveTasks.RemoveAll(candidate => candidate.IsCompleted);
            _receiveTasks.Add(task);
        }
    }

    private void RemoveOutbound(string key, TcpConnection connection)
    {
        var collection = (ICollection<KeyValuePair<string, TcpConnection>>)_outboundConnections;
        collection.Remove(new KeyValuePair<string, TcpConnection>(key, connection));
        connection.Dispose();
    }

    private void RemoveInbound(long id, TcpConnection connection)
    {
        var collection = (ICollection<KeyValuePair<long, TcpConnection>>)_inboundConnections;
        collection.Remove(new KeyValuePair<long, TcpConnection>(id, connection));
        connection.Dispose();
    }

    private void ValidateConfiguration(ProtocolConfiguration config)
    {
        if (config.LocalPort is < 0 or > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(config.LocalPort));
        if (config.RemoteEndPoint.Port == 0) throw new ArgumentException("RemoteEndPoint must use a non-zero port.", nameof(config));
        if (config.MaxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(config.MaxPayloadBytes));
        if (config.MaxInboundConnections <= 0) throw new ArgumentOutOfRangeException(nameof(config.MaxInboundConnections));
        if (!_serializers.ContainsKey(config.SerializationProtocol))
        {
            throw new ArgumentException($"Serializer {config.SerializationProtocol} is not registered.", nameof(config));
        }
    }
}