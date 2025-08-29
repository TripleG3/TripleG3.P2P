using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using TripleG3.P2P.Maui.Core;

namespace TripleG3.P2P.Maui.Serialization;

internal sealed class NoneMessageSerializer : IMessageSerializer
{
    public SerializationProtocol Protocol => SerializationProtocol.None;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, int Order)[]> _cache = new();
    private static readonly byte[] Delimiter = Encoding.UTF8.GetBytes("@-@");

    public byte[] Serialize<T>(T value)
    {
        // Attribute based simple delimited serialization using @-@ between ordered properties.
        if (value is null) return Array.Empty<byte>();
        var type = value.GetType();
        var props = _cache.GetOrAdd(type, t => t.GetProperties()
            .Select(p => (p, attr: p.GetCustomAttribute<Attributes.UdpAttribute>()))
            .Where(x => x.attr != null)
            .Select(x => (x.p, x.attr!.Order ?? int.MaxValue))
            .OrderBy(x => x.Item2)
            .ToArray());
        if (props.Length == 0) return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);

        // Pre-calc sizes
        var stringValues = new string[props.Length];
        int total = 0;
        for (int i = 0; i < props.Length; i++)
        {
            var v = props[i].Prop.GetValue(value);
            var s = v?.ToString() ?? string.Empty;
            stringValues[i] = s;
            total += Encoding.UTF8.GetByteCount(s);
            if (i < props.Length - 1) total += Delimiter.Length;
        }
        var buffer = new byte[total];
        var span = buffer.AsSpan();
        int offset = 0;
        for (int i = 0; i < stringValues.Length; i++)
        {
            var s = stringValues[i];
            offset += Encoding.UTF8.GetBytes(s, span[offset..]);
            if (i < stringValues.Length - 1)
            {
                Delimiter.CopyTo(span[offset..]);
                offset += Delimiter.Length;
            }
        }
        return buffer;
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var obj = Deserialize(typeof(T), data);
        return obj is T t ? t : default;
    }

    public object? Deserialize(Type type, ReadOnlySpan<byte> data)
    {
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor == null) return null;
        var parameters = ctor.GetParameters();
        var parts = Split(data, Delimiter, parameters.Length);
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var pType = parameters[i].ParameterType;
            string str = string.Empty;
            if (i < parts.Length)
            {
                var (start, len) = parts[i];
                if (start >= 0 && len >= 0 && start + len <= data.Length)
                    str = Encoding.UTF8.GetString(data.Slice(start, len));
            }
            args[i] = ConvertFromString(str, pType);
        }
        return ctor.Invoke(args);
    }

    private static object? ConvertFromString(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
        if (targetType == typeof(Guid)) return Guid.Parse(value);
        if (string.IsNullOrEmpty(value)) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        return Convert.ChangeType(value, targetType);
    }

    private static (int start, int length)[] Split(ReadOnlySpan<byte> data, ReadOnlySpan<byte> delimiter, int maxParts)
    {
        var temp = new List<(int, int)>(Math.Min(maxParts, 8));
        int start = 0;
        while (start <= data.Length)
        {
            if (temp.Count == maxParts - 1) // last part consumes rest
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
        return temp.ToArray();
    }
}
