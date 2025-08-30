using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text;
using TripleG3.P2P.Core;
using TripleG3.P2P.Serialization;
using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Tcp;

/// <summary>
/// TCP implementation of <see cref="ISerialBus"/>. Maintains a listener (if configured via <see cref="ProtocolConfiguration.LocalPort"/>)
/// and outgoing connections to the configured <see cref="ProtocolConfiguration.RemoteEndPoint"/> plus any <see cref="ProtocolConfiguration.BroadcastEndPoints"/>.
/// Connections are established lazily on first send; inbound accepted sockets are added to the connection set for fan-out.
/// </summary>
public sealed class TcpSerialBus : ISerialBus, IDisposable
{
    private readonly IReadOnlyDictionary<SerializationProtocol, IMessageSerializer> _serializers;
    private ProtocolConfiguration? _config;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;

    private sealed record SubscriptionEntry(Type Type, Delegate Handler);
    private readonly ConcurrentDictionary<string, List<SubscriptionEntry>> _subscriptions = new(StringComparer.Ordinal);

    // Active outbound / inbound connections
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1,1);

    public TcpSerialBus(IEnumerable<IMessageSerializer> serializers)
        => _serializers = serializers.ToDictionary(s => s.Protocol, s => s);

    public bool IsListening => _listener != null;

    public ValueTask StartListeningAsync(ProtocolConfiguration config, CancellationToken cancellationToken = default)
    {
        if (IsListening) return ValueTask.CompletedTask;
        _config = config;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, config.LocalPort);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        if (_listener is null) return;
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                var key = ((IPEndPoint)client.Client.RemoteEndPoint!).ToString();
                _clients[key] = client;
                _ = ReceiveLoopAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch { client?.Dispose(); }
        }
    }

    private async Task EnsureConnectionsAsync()
    {
        if (_config is null) return;
        await _connectLock.WaitAsync();
        try
        {
            async Task ConnectToAsync(IPEndPoint ep)
            {
                var key = ep.ToString();
                if (_clients.ContainsKey(key)) return;
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(ep.Address, ep.Port);
                    _clients[key] = client;
                    _ = ReceiveLoopAsync(client, _cts!.Token);
                }
                catch
                {
                    client.Dispose();
                }
            }
            await ConnectToAsync(_config.RemoteEndPoint);
            foreach (var ep in _config.BroadcastEndPoints)
                await ConnectToAsync(ep);
        }
        finally { _connectLock.Release(); }
    }

    private async Task ReceiveLoopAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var headerBuffer = new byte[8];
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await ReadExactAsync(stream, headerBuffer, token)) break;
                int length = BitConverter.ToInt32(headerBuffer, 0);
                short messageType = BitConverter.ToInt16(headerBuffer, 4);
                short protoVal = BitConverter.ToInt16(headerBuffer, 6);
                if (length <= 0 || length > 10_000_000) break; // sanity
                var payload = new byte[length];
                if (!await ReadExactAsync(stream, payload, token)) break;
                ProcessIncoming(payload, (SerializationProtocol)protoVal);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
        try
        {
            client.Close();
            var key = ((IPEndPoint)client.Client.RemoteEndPoint!).ToString();
            _clients.TryRemove(key, out _);
        }
        catch { }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private void ProcessIncoming(byte[] payload, SerializationProtocol protocol)
    {
        if (!_serializers.TryGetValue(protocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];
        var protocolName = TryExtractTypeName(payload, protocol);
        if (string.IsNullOrEmpty(protocolName)) return;
        if (!_subscriptions.TryGetValue(protocolName, out var entries)) return;

        foreach (var entry in entries)
        {
            var envelopeType = typeof(Envelope<>).MakeGenericType(entry.Type);
            object? envelopeObj = null;
            try { envelopeObj = serializer.Deserialize(envelopeType, payload); } catch { continue; }
            if (envelopeObj is null) continue;
            var messageProp = envelopeType.GetProperty("Message");
            if (messageProp?.GetValue(envelopeObj) is not { } msg) continue;
            try { entry.Handler.DynamicInvoke(msg); } catch { }
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
        catch { }
        return null;
    }

    private static string GetProtocolTypeName(Type type)
    {
        var attr = type.GetCustomAttribute<UdpMessageAttribute>();
        if (attr?.Name is { Length: > 0 } n) return n;
        return type.Name;
    }

    public void SubscribeTo<T>(Action<T> handler)
    {
        var protocolName = GetProtocolTypeName(typeof(T));
        var list = _subscriptions.GetOrAdd(protocolName, _ => []);
        list.Add(new SubscriptionEntry(typeof(T), handler));
    }

    public async ValueTask SendAsync<T>(T message, MessageType messageType = MessageType.Data, CancellationToken cancellationToken = default)
    {
        if (_config is null) throw new InvalidOperationException("Configuration not set. Call StartListeningAsync first.");
        if (!_serializers.TryGetValue(_config.SerializationProtocol, out var serializer)) serializer = _serializers[SerializationProtocol.None];

        await EnsureConnectionsAsync();

        var typeName = GetProtocolTypeName(typeof(T));
        var envelope = new Envelope<T>(typeName, message);
        byte[] body = serializer.Serialize(envelope);
        var buffer = new byte[8 + body.Length];
        BitConverter.GetBytes(body.Length).CopyTo(buffer, 0);
        BitConverter.GetBytes((short)messageType).CopyTo(buffer, 4);
        BitConverter.GetBytes((short)_config.SerializationProtocol).CopyTo(buffer, 6);
        body.CopyTo(buffer, 8);

        foreach (var kv in _clients.ToArray())
        {
            try
            {
                if (!kv.Value.Connected) { _clients.TryRemove(kv.Key, out _); continue; }
                var stream = kv.Value.GetStream();
                await stream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public async ValueTask CloseConnectionAsync()
    {
        try { _cts?.Cancel(); } catch { }
        _listener?.Stop();
        foreach (var c in _clients.Values) { try { c.Close(); } catch { } }
        _clients.Clear();
        await Task.Yield();
    }

    public void Dispose()
    {
        _ = CloseConnectionAsync();
        _cts?.Dispose();
        _connectLock.Dispose();
    }
}