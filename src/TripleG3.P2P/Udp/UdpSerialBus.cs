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

namespace TripleG3.P2P.Udp;

/// <summary>
/// UDP implementation of <see cref="ISerialBus"/> with strict frame validation and configurable fan-out.
/// </summary>
public sealed class UdpSerialBus : ISubscriptionSerialBus, IDisposable, IAsyncDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly IReadOnlyDictionary<SerializationProtocol, IMessageSerializer> _serializers;
    private readonly ILogger<UdpSerialBus> _logger;
    private readonly ConcurrentDictionary<string, (Guid Id, Type Type, Delegate Handler)[]> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private ProtocolConfiguration? _config;
    private Task? _receiveTask;
    private int _disposed;

    public UdpSerialBus(IEnumerable<IMessageSerializer> serializers)
        : this(serializers, NullLogger<UdpSerialBus>.Instance)
    {
    }

    public UdpSerialBus(IEnumerable<IMessageSerializer> serializers, ILogger<UdpSerialBus> logger)
    {
        ArgumentNullException.ThrowIfNull(serializers);
        ArgumentNullException.ThrowIfNull(logger);
        _serializers = serializers.ToDictionary(serializer => serializer.Protocol, serializer => serializer);
        _logger = logger;
    }

    public bool IsListening => Volatile.Read(ref _udpClient) is not null;

    public ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration(config);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_udpClient is not null) return ValueTask.CompletedTask;

            _config = config;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _udpClient = new UdpClient(new IPEndPoint(config.LocalAddress, config.LocalPort));
            _receiveTask = ReceiveLoopAsync(_udpClient, _cts.Token);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var client = Volatile.Read(ref _udpClient) ?? throw new InvalidOperationException("Not listening. Call StartListeningAsync first.");
        var config = _config ?? throw new InvalidOperationException("Configuration not set.");
        if (!_serializers.TryGetValue(config.SerializationProtocol, out var serializer))
        {
            throw new InvalidOperationException($"Serializer {config.SerializationProtocol} is not registered.");
        }

        if (!Enum.IsDefined(messageType))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        var typeName = GetProtocolTypeName(typeof(T));
        var body = serializer.Serialize(new Envelope<T>(typeName, message));
        if (body.Length > config.MaxPayloadBytes)
        {
            throw new InvalidDataException($"Serialized payload exceeds the {config.MaxPayloadBytes}-byte configured limit.");
        }

        var frame = new byte[UdpHeader.Size + body.Length];
        new UdpHeader(body.Length, (short)messageType, config.SerializationProtocol).Write(frame);
        body.CopyTo(frame.AsSpan(UdpHeader.Size));

        var endpoints = new[] { config.RemoteEndPoint }
            .Concat(config.BroadcastEndPoints)
            .DistinctBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)
            .ToArray();
        var failures = new List<Exception>();
        var successCount = 0;

        foreach (var endpoint in endpoints)
        {
            try
            {
                await client.SendAsync(frame.AsMemory(), endpoint, cancellationToken).ConfigureAwait(false);
                successCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
            {
                failures.Add(exception);
                _logger.LogWarning(exception, "UDP send to {RemoteEndPoint} failed.", endpoint);
            }
        }

        if (successCount == 0)
        {
            throw new AggregateException("UDP send failed for every configured endpoint.", failures);
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "UDP broadcast completed partially: {Succeeded} of {Attempted} endpoints succeeded.",
                successCount,
                endpoints.Length);
        }
    }

    public async ValueTask CloseConnectionAsync()
    {
        UdpClient? client;
        CancellationTokenSource? cts;
        Task? receiveTask;

        lock (_lifecycleGate)
        {
            client = _udpClient;
            cts = _cts;
            receiveTask = _receiveTask;
            _udpClient = null;
            _cts = null;
            _receiveTask = null;
            _config = null;
        }

        if (client is null) return;
        cts?.Cancel();
        client.Dispose();
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        UdpClient? client;
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            client = _udpClient;
            cts = _cts;
            _udpClient = null;
            _cts = null;
            _receiveTask = null;
            _config = null;
        }

        cts?.Cancel();
        client?.Dispose();
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseConnectionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ProcessIncoming(result.Buffer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is SocketException or InvalidDataException or JsonException or DecoderFallbackException)
            {
                _logger.LogWarning(exception, "UDP datagram was rejected.");
            }
        }
    }

    private void ProcessIncoming(byte[] buffer)
    {
        if (buffer.Length < UdpHeader.Size)
        {
            _logger.LogWarning("Rejected UDP frame with a truncated header ({Length} bytes).", buffer.Length);
            return;
        }

        var header = UdpHeader.Read(buffer);
        var availablePayloadBytes = buffer.Length - UdpHeader.Size;
        if (header.Length < 0 || header.Length != availablePayloadBytes || (_config is { } config && header.Length > config.MaxPayloadBytes))
        {
            _logger.LogWarning("Rejected UDP frame with invalid payload length {PayloadLength}; datagram contains {AvailableLength} bytes.", header.Length, availablePayloadBytes);
            return;
        }

        if (!Enum.IsDefined((MessageType)header.MessageType))
        {
            _logger.LogWarning("Rejected UDP frame with unknown message type {MessageType}.", header.MessageType);
            return;
        }

        if (!_serializers.TryGetValue(header.SerializationProtocol, out var serializer))
        {
            _logger.LogWarning("Rejected UDP frame with unknown serialization protocol {Protocol}.", (short)header.SerializationProtocol);
            return;
        }

        var payload = buffer.AsSpan(UdpHeader.Size, header.Length);
        var protocolName = TryExtractTypeName(payload, header.SerializationProtocol);
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
                _logger.LogWarning(exception, "UDP subscriber for {MessageType} failed.", entry.Type);
            }
        }
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