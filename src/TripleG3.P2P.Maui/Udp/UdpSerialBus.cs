using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TripleG3.P2P.Maui.Attributes;
using TripleG3.P2P.Maui.Core;
using TripleG3.P2P.Maui.Serialization;

namespace TripleG3.P2P.Maui.Udp;

internal sealed class UdpSerialBus : ISerialBus, IDisposable
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

    public async ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default)
    {
        if (IsListening) return;
        _config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _udpClient = new UdpClient(config.LocalPort);
        await Task.Run(() => ReceiveLoopAsync(_cts.Token));
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

    private void ProcessIncoming(byte[] buffer)
    {
        if (buffer.Length < UdpHeader.Size) return;
        var header = UdpHeader.Read(buffer);
        var payload = buffer.AsSpan(UdpHeader.Size, header.Length);
        var serializer = _serializers.TryGetValue(header.SerializationProtocol, out var ser) ? ser : _serializers[SerializationProtocol.None];

        foreach (var kvp in _subscriptions)
        {
            var targetType = kvp.Key;
            object? obj;
            if (serializer.Protocol == SerializationProtocol.None)
            {
                obj = serializer.Deserialize(targetType, payload);
            }
            else
            {
                obj = serializer.Deserialize(targetType, payload);
            }
            if (obj is null) continue;
            foreach (var d in kvp.Value)
            {
                try { d.DynamicInvoke(obj); } catch { /* ignore */ }
            }
        }
    }

    public void SubscribeTo<T>(Action<T> handler)
    {
        var list = _subscriptions.GetOrAdd(typeof(T), _ => new List<Delegate>());
        list.Add(handler);
    }

    public async ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default)
    {
        if (_udpClient is null) throw new InvalidOperationException("Not listening. StartListeningAsync first.");
        if (_config is null) throw new InvalidOperationException("Configuration not set.");
        var protocol = _config.SerializationProtocol;
        if (!_serializers.TryGetValue(protocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];

        byte[] body;
        if (protocol == SerializationProtocol.None && message is not string && message is not byte[])
        {
            // attribute based serialization if decorated
            body = serializer.Serialize(message!);
        }
        else
        {
            body = serializer.Serialize(message!);
        }
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
}
