using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Serialization;

namespace TripleG3.P2P.Udp;

/// <summary>
/// UDP implementation of <see cref="ISerialBus"/> providing fire-and-forget message transmission
/// with attribute-driven dispatch and pluggable serialization protocols.
/// </summary>
public sealed partial class UdpSerialBus(IEnumerable<IMessageSerializer> serializers) : ISerialBus, IDisposable
{
    private readonly IReadOnlyDictionary<SerializationProtocol, IMessageSerializer> _serializers = serializers.ToDictionary(s => s.Protocol, s => s);

    private sealed record SubscriptionEntry(Type Type, Delegate Handler);
    private readonly ConcurrentDictionary<string, List<SubscriptionEntry>> _subscriptions = new(StringComparer.Ordinal);

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private ProtocolConfiguration? _config;

    /// <summary>
    /// True if a UDP client is active and listening.
    /// </summary>
    public bool IsListening => _udpClient != null;

    /// <inheritdoc />
    public ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default)
    {
        if (IsListening) return ValueTask.CompletedTask;
        _config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _udpClient = new UdpClient(config.LocalPort);
        _ = ReceiveLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_udpClient is null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(token);
                ProcessIncoming(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow to keep loop alive; replace with logging hook if desired.
            }
        }
    }

    private void ProcessIncoming(byte[] buffer)
    {
        if (buffer.Length < UdpHeader.Size) return;
        var header = UdpHeader.Read(buffer);
        var payload = buffer.AsSpan(UdpHeader.Size, header.Length);
        if (!_serializers.TryGetValue(header.SerializationProtocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];

        var protocolName = TryExtractTypeName(payload, header.SerializationProtocol);
        if (string.IsNullOrEmpty(protocolName)) return;
        if (!_subscriptions.TryGetValue(protocolName, out var entries)) return;

        foreach (var entry in entries)
        {
            var envelopeType = typeof(Envelope<>).MakeGenericType(entry.Type);
            object? envelopeObj = null;
            try
            {
                envelopeObj = serializer.Deserialize(envelopeType, payload);
            }
            catch
            {
                continue;
            }
            if (envelopeObj is null) continue;
            var messageProp = envelopeType.GetProperty("Message");
            if (messageProp is null) continue;
            var messageValue = messageProp.GetValue(envelopeObj);
            if (messageValue is null) continue;
            try { entry.Handler.DynamicInvoke(messageValue); } catch { }
        }
    }

    private static string? TryExtractTypeName(ReadOnlySpan<byte> payload, SerializationProtocol protocol)
    {
        try
        {
            if (protocol == SerializationProtocol.None)
            {
                var delimiter = Encoding.UTF8.GetBytes("@-@");
                int idx = payload.IndexOf(delimiter);
                if (idx < 0) return Encoding.UTF8.GetString(payload);
                return Encoding.UTF8.GetString(payload[..idx]);
            }
            else if (protocol == SerializationProtocol.JsonRaw)
            {
                using var doc = JsonDocument.Parse(payload.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("typeName", out var tn)) return tn.GetString();
                if (root.TryGetProperty("TypeName", out var tn2)) return tn2.GetString();
            }
        }
        catch
        {
        }
        return null;
    }

    private static string GetProtocolTypeName(Type type)
    {
        var attr = type.GetCustomAttribute<UdpMessageAttribute>();
        if (attr?.Name is { Length: > 0 } n) return n;
        return type.Name;
    }

    /// <inheritdoc />
    public void SubscribeTo<T>(Action<T> handler)
    {
        var protocolName = GetProtocolTypeName(typeof(T));
        var list = _subscriptions.GetOrAdd(protocolName, _ => []);
        list.Add(new SubscriptionEntry(typeof(T), handler));
    }

    /// <inheritdoc />
    public async ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default)
    {
        if (_udpClient is null) throw new InvalidOperationException("Not listening. StartListeningAsync first.");
        if (_config is null) throw new InvalidOperationException("Configuration not set.");
        var protocol = _config.SerializationProtocol;
        if (!_serializers.TryGetValue(protocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];

        var typeName = GetProtocolTypeName(typeof(T));
        var envelope = new Envelope<T>(typeName, message);
        byte[] body = serializer.Serialize(envelope);
        var header = new UdpHeader(body.Length, (short)messageType, protocol);
        var buffer = new byte[UdpHeader.Size + body.Length];
        header.Write(buffer);
        body.CopyTo(buffer.AsSpan(UdpHeader.Size));
        // Build unique endpoint list: primary + broadcast extras
        if (_config.RemoteEndPoint is null) throw new InvalidOperationException("RemoteEndPoint not configured.");
        // De-duplicate using hash set of endpoint strings
        var sentTo = new HashSet<string>(StringComparer.Ordinal);
        async Task SendToAsync(IPEndPoint ep)
        {
            var key = ep.ToString();
            if (!sentTo.Add(key)) return; // already scheduled
            try
            {
                await _udpClient.SendAsync(buffer, buffer.Length, ep);
            }
            catch
            {
                // Swallow; consider logging hook
            }
        }

        await SendToAsync(_config.RemoteEndPoint);
        if (_config.BroadcastEndPoints.Count > 0)
        {
            // Send sequentially to maintain order; could be parallel if needed but no awaits inside loop aside from network
            foreach (var ep in _config.BroadcastEndPoints)
            {
                await SendToAsync(ep);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseConnectionAsync()
    {
        try { _cts?.Cancel(); } catch { }
        await Task.Yield();
        _udpClient?.Dispose();
        _udpClient = null;
    }

    /// <summary>
    /// Disposes socket and cancels the receive loop.
    /// </summary>
    public void Dispose()
    {
        _udpClient?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void ProcessEnvelope<T>(Envelope<T> envelope) { }
}
