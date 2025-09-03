using System.Collections.Concurrent;
using System.Reflection;
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

    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, int Order)[]> _cache = new();
    private static readonly byte[] Delimiter = Encoding.UTF8.GetBytes("@-@");

    public byte[] Serialize<T>(T value)
        => SerializeInternal(value, typeof(T));

    private byte[] SerializeInternal(object? value, Type? declaredType = null)
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

        var props = _cache.GetOrAdd(type, t => [.. t.GetProperties()
            .Select(p => (p, attr: p.GetCustomAttribute<Attributes.UdpAttribute>()))
            .Where(x => x.attr != null)
            .Select(x => (x.p, x.attr!.Order ?? int.MaxValue))
            .OrderBy(x => x.Item2)]);
        if (props.Length == 0)
        {
            return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
        }

        var serializedProps = new byte[props.Length][];
        int total = 0;
        for (int i = 0; i < props.Length; i++)
        {
            var v = props[i].Prop.GetValue(value);
            byte[] bytes = v is null
                ? []
                : IsPrimitiveLike(v.GetType())
                    ? Encoding.UTF8.GetBytes(v.ToString() ?? string.Empty)
                    : SerializeInternal(v);
            serializedProps[i] = bytes;
            total += bytes.Length;
            if (i < props.Length - 1) total += Delimiter.Length;
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

    private static bool IsPrimitiveLike(Type t)
        => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(decimal);

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var obj = Deserialize(typeof(T), data);
        return obj is T t ? t : default;
    }

    public object? Deserialize(Type type, ReadOnlySpan<byte> data)
    {
        if (type == typeof(string)) return Encoding.UTF8.GetString(data);
        if (IsPrimitiveLike(type) && type != typeof(string))
        {
            var strVal = Encoding.UTF8.GetString(data);
            return ConvertFromString(strVal, type);
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
            var ctorEnv = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 2);
            if (ctorEnv is null) return null;
            return ctorEnv.Invoke([typeName, messageObj]);
        }

        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor == null) return null;
        var parameters = ctor.GetParameters();
        if (parameters.Length == 0) return ctor.Invoke(null);

        var parts = Split(data, Delimiter, parameters.Length);
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var pType = parameters[i].ParameterType;
            ReadOnlySpan<byte> slice = [];
            if (i < parts.Length)
            {
                var (start, len) = parts[i];
                if (start >= 0 && len >= 0 && start + len <= data.Length)
                    slice = data.Slice(start, len);
            }

            if (slice.IsEmpty)
            {
                args[i] = pType.IsValueType ? Activator.CreateInstance(pType) : null;
                continue;
            }

            args[i] = IsPrimitiveLike(pType)
                ? ConvertFromString(Encoding.UTF8.GetString(slice), pType)
                : Deserialize(pType, slice);
        }
        return ctor.Invoke(args);
    }

    private static object? ConvertFromString(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType.IsEnum) return string.IsNullOrEmpty(value) ? Activator.CreateInstance(targetType) : Enum.Parse(targetType, value, true);
        if (targetType == typeof(Guid)) return string.IsNullOrEmpty(value) ? Guid.Empty : Guid.Parse(value);
        if (string.IsNullOrEmpty(value)) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        return Convert.ChangeType(value, targetType);
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
