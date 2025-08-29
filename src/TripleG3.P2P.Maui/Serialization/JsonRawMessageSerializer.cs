using System.Text;
using System.Text.Json;
using TripleG3.P2P.Maui.Core;

namespace TripleG3.P2P.Maui.Serialization;

internal sealed class JsonRawMessageSerializer : IMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public SerializationProtocol Protocol => SerializationProtocol.JsonRaw;

    public byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize<T>(data, Options);

    public object? Deserialize(Type type, ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize(data, type, Options);
}
