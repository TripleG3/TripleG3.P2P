using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TripleG3.P2P.Maui.Attributes;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.Serialization;

namespace TripleG3.P2P.Maui.Udp;

internal sealed partial class UdpSerialBus : ISerialBus, IDisposable
{
    private readonly IReadOnlyDictionary<SerializationProtocol, IMessageSerializer> _serializers;
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subscriptions = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private ProtocolConfiguration? _config;

    public bool IsListening => _udpClient != null;

    public UdpSerialBus(IEnumerable<IMessageSerializer> serializers)
    {
        _serializers = serializers.ToDictionary(s => s.Protocol, s => s);
    }

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
                var result = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
                ProcessIncoming(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // swallow for now (fire & forget). Could add logging hook.
            }
        }
    }

    private static readonly MethodInfo _processEnvelopeGeneric = typeof(UdpSerialBus).GetMethod(nameof(ProcessEnvelope), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private void ProcessIncoming(byte[] buffer)
    {
        if (buffer.Length < UdpHeader.Size) return;
        var header = UdpHeader.Read(buffer);
        var payload = buffer.AsSpan(UdpHeader.Size, header.Length);
        var serializer = _serializers.TryGetValue(header.SerializationProtocol, out var ser) ? ser : _serializers[SerializationProtocol.None];

        // First attempt to deserialize an Envelope<string> just to extract the TypeName (fast path for json).
        // We don't know the underlying generic argument type yet; we'll scan subscriptions for matching type names.
        foreach (var kvp in _subscriptions)
        {
            var targetClrType = kvp.Key; // This is the T clients subscribed with.
            // Build Envelope<T>
            var envelopeType = typeof(Envelope<>).MakeGenericType(targetClrType);
            object? envelopeObj;
            try
            {
                envelopeObj = serializer.Deserialize(envelopeType, payload);
            }
            catch
            {
                continue; // deserialize failure for this T, try next.
            }
            if (envelopeObj is null) continue;
            var typeNameProp = envelopeType.GetProperty("TypeName");
            var messageProp = envelopeType.GetProperty("Message");
            if (typeNameProp == null || messageProp == null) continue;
            var typeName = typeNameProp.GetValue(envelopeObj) as string;
            if (string.IsNullOrEmpty(typeName)) continue;

            // Compare desired protocol name for the subscribed type
            var expectedName = GetProtocolTypeName(targetClrType);
            if (!string.Equals(typeName, expectedName, StringComparison.Ordinal))
                continue;

            var messageValue = messageProp.GetValue(envelopeObj);
            if (messageValue is null) continue;
            foreach (var d in kvp.Value)
            {
                try { d.DynamicInvoke(messageValue); } catch { }
            }
        }
    }

    private static string GetProtocolTypeName(Type type)
    {
        var attr = type.GetCustomAttribute<UdpMessageAttribute>();
        if (attr?.Name is { Length: > 0 } n) return n;
        // Generic variant base class check already handled via attribute inheritance.
        return type.Name;
    }

    public void SubscribeTo<T>(Action<T> handler)
    {
        var list = _subscriptions.GetOrAdd(typeof(T), _ => []);
        list.Add(handler);
    }

    public async ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default)
    {
        if (_udpClient is null) throw new InvalidOperationException("Not listening. StartListeningAsync first.");
        if (_config is null) throw new InvalidOperationException("Configuration not set.");
        var protocol = _config.SerializationProtocol;
        if (!_serializers.TryGetValue(protocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];

        // Wrap message in Envelope<T>
        var typeName = GetProtocolTypeName(typeof(T));
        var envelope = new Envelope<T>(typeName, message);

        byte[] body = serializer.Serialize(envelope);
        var header = new UdpHeader(body.Length, (short)messageType, protocol);
        var buffer = new byte[UdpHeader.Size + body.Length];
        header.Write(buffer);
        body.CopyTo(buffer.AsSpan(UdpHeader.Size));
        await _udpClient.SendAsync(buffer, buffer.Length, _config.RemoteEndPoint);
    }

    public async ValueTask CloseConnectionAsync()
    {
        try { _cts?.Cancel(); } catch { }
        await Task.Yield();
        _udpClient?.Dispose();
        _udpClient = null;
    }

    public void Dispose()
    {
        _udpClient?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    // Placeholder for reflection acquired generic method (not used directly but kept for potential optimization)
    private void ProcessEnvelope<T>(Envelope<T> envelope) { }
}
