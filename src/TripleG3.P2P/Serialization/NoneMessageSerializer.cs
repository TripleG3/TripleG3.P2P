using System.Text;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.Serialization;

/// <summary>
/// Attribute-driven, delimiter (@-@) based serializer capturing only properties marked with <c>[Udp]</c>.
/// Provides a compact textual wire format with minimal allocations and recursive support for nested annotated types.
/// </summary>
internal sealed class NoneMessageSerializer : IMessageSerializer
{
    public SerializationProtocol Protocol => SerializationProtocol.None;

    private static readonly byte[] Delimiter = Encoding.UTF8.GetBytes("@-@");

    public byte[] Serialize<T>(T value)
        => SerializeInternal(value);

    private byte[] SerializeInternal(object? value)
    {
        if (value is null) return [];
        var type = value.GetType();

        // Envelope<T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Envelope<>))
        {
            var typeNameProp = type.GetProperty("TypeName");
            var messageProp = type.GetProperty("Message");
            var typeName = typeNameProp?.GetValue(value) as string ?? string.Empty;
            var messageObj = messageProp?.GetValue(value);
            var typeNameBytes = Encoding.UTF8.GetBytes(typeName);
            if (messageObj is null) return typeNameBytes;
            var messageBytes = SerializeInternal(messageObj);
            if (messageBytes.Length == 0) return typeNameBytes;
            var buffer = new byte[typeNameBytes.Length + Delimiter.Length + messageBytes.Length];
            var span = buffer.AsSpan();
            typeNameBytes.CopyTo(span);
            Delimiter.CopyTo(span[typeNameBytes.Length..]);
            messageBytes.CopyTo(span[(typeNameBytes.Length + Delimiter.Length)..]);
            return buffer;
        }

        if (PrimitiveValueConverter.IsSupported(type))
        {
            return Encoding.UTF8.GetBytes(PrimitiveValueConverter.Format(value, type));
        }

        var contract = SerializationContract.For(type);
        var properties = contract.Properties;

        var serializedProps = new byte[properties.Count][];
        int total = 0;
        for (int i = 0; i < properties.Count; i++)
        {
            var propertyValue = properties[i].GetValue(value);
            byte[] bytes = propertyValue is null
                ? []
                : PrimitiveValueConverter.IsSupported(properties[i].PropertyType)
                    ? Encoding.UTF8.GetBytes(PrimitiveValueConverter.Format(propertyValue, properties[i].PropertyType))
                    : SerializeInternal(propertyValue);
            serializedProps[i] = bytes;
            total += bytes.Length;
            if (i < properties.Count - 1) total += Delimiter.Length;
        }
        var buffer2 = new byte[total];
        var span2 = buffer2.AsSpan();
        int offset = 0;
        for (int i = 0; i < serializedProps.Length; i++)
        {
            var bytes = serializedProps[i];
            bytes.CopyTo(span2[offset..]);
            offset += bytes.Length;
            if (i < serializedProps.Length - 1)
            {
                Delimiter.CopyTo(span2[offset..]);
                offset += Delimiter.Length;
            }
        }
        return buffer2;
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var obj = Deserialize(typeof(T), data);
        return obj is T t ? t : default;
    }

    public object? Deserialize(Type type, ReadOnlySpan<byte> data)
    {
        if (PrimitiveValueConverter.IsSupported(type))
        {
            var strVal = Encoding.UTF8.GetString(data);
            return PrimitiveValueConverter.Parse(strVal, type);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Envelope<>))
        {
            var innerType = type.GetGenericArguments()[0];
            int idx = data.IndexOf(Delimiter);
            string typeName;
            ReadOnlySpan<byte> messageData;
            if (idx < 0)
            {
                typeName = Encoding.UTF8.GetString(data);
                messageData = [];
            }
            else
            {
                typeName = Encoding.UTF8.GetString(data[..idx]);
                messageData = data[(idx + Delimiter.Length)..];
            }
            object? messageObj = null;
            if (!messageData.IsEmpty)
            {
                messageObj = Deserialize(innerType, messageData);
            }
            return Activator.CreateInstance(type, typeName, messageObj);
        }

        var contract = SerializationContract.For(type);
        var properties = contract.Properties;

        var parts = Split(data, Delimiter, properties.Count);
        var values = new object?[properties.Count];
        for (int i = 0; i < properties.Count; i++)
        {
            var propertyType = properties[i].PropertyType;
            ReadOnlySpan<byte> slice = [];
            if (i < parts.Length)
            {
                var (start, len) = parts[i];
                if (start >= 0 && len >= 0 && start + len <= data.Length)
                    slice = data.Slice(start, len);
            }

            if (slice.IsEmpty)
            {
                values[i] = PrimitiveValueConverter.DefaultValue(propertyType);
                continue;
            }

            values[i] = PrimitiveValueConverter.IsSupported(propertyType)
                ? PrimitiveValueConverter.Parse(Encoding.UTF8.GetString(slice), propertyType)
                : Deserialize(propertyType, slice);
        }
        return contract.Create(values);
    }

    private static (int start, int length)[] Split(ReadOnlySpan<byte> data, ReadOnlySpan<byte> delimiter, int maxParts)
    {
        var temp = new List<(int, int)>(Math.Min(maxParts, 8));
        int start = 0;
        while (start <= data.Length)
        {
            if (temp.Count == maxParts - 1)
            {
                temp.Add((start, data.Length - start));
                break;
            }
            int idx = data[start..].IndexOf(delimiter);
            if (idx < 0)
            {
                temp.Add((start, data.Length - start));
                break;
            }
            temp.Add((start, idx));
            start += idx + delimiter.Length;
        }
        return [.. temp];
    }
}
