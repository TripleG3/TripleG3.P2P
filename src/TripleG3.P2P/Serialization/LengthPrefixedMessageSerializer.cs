using System.Text;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.Serialization;

internal sealed class LengthPrefixedMessageSerializer : IMessageSerializer
{
    private const byte FormatVersion = 1;
    private const int MaximumValueBytes = 10 * 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public SerializationProtocol Protocol => SerializationProtocol.LengthPrefixed;

    public byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, true);
        writer.Write(FormatVersion);
        WriteRoot(writer, typeof(T), value);
        writer.Flush();
        return stream.ToArray();
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var value = Deserialize(typeof(T), data);
        return value is T typed ? typed : default;
    }

    public object? Deserialize(Type type, ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), false);
        using var reader = new BinaryReader(stream, StrictUtf8, true);
        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported length-prefixed format version {version}.");
        }

        var value = ReadRoot(reader, type);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Length-prefixed payload contains trailing bytes.");
        }

        return value;
    }

    internal static string? TryExtractTypeName(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), false);
            using var reader = new BinaryReader(stream, StrictUtf8, true);
            if (reader.ReadByte() != FormatVersion) return null;
            return ReadUtf8(reader);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static void WriteRoot(BinaryWriter writer, Type type, object? value)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Envelope<>))
        {
            var typeName = type.GetProperty(nameof(Envelope<object>.TypeName))?.GetValue(value) as string ?? string.Empty;
            var message = type.GetProperty(nameof(Envelope<object>.Message))?.GetValue(value);
            WriteUtf8(writer, typeName);
            WriteValue(writer, type.GetGenericArguments()[0], message);
            return;
        }

        WriteValue(writer, type, value);
    }

    private static object? ReadRoot(BinaryReader reader, Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Envelope<>))
        {
            var typeName = ReadUtf8(reader);
            var message = ReadValue(reader, type.GetGenericArguments()[0]);
            return Activator.CreateInstance(type, typeName, message);
        }

        return ReadValue(reader, type);
    }

    private static void WriteValue(BinaryWriter writer, Type declaredType, object? value)
    {
        if (value is null)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)1);
        if (PrimitiveValueConverter.IsSupported(declaredType))
        {
            WriteUtf8(writer, PrimitiveValueConverter.Format(value, declaredType));
            return;
        }

        var contract = SerializationContract.For(declaredType);
        writer.Write(contract.Properties.Count);
        foreach (var property in contract.Properties)
        {
            WriteValue(writer, property.PropertyType, property.GetValue(value));
        }
    }

    private static object? ReadValue(BinaryReader reader, Type declaredType)
    {
        var marker = reader.ReadByte();
        if (marker == 0)
        {
            if (declaredType.IsValueType && Nullable.GetUnderlyingType(declaredType) is null)
            {
                throw new InvalidDataException($"A null value is not valid for {declaredType.FullName}.");
            }

            return null;
        }

        if (marker != 1)
        {
            throw new InvalidDataException($"Invalid null marker {marker}.");
        }

        if (PrimitiveValueConverter.IsSupported(declaredType))
        {
            return PrimitiveValueConverter.Parse(ReadUtf8(reader), declaredType);
        }

        var contract = SerializationContract.For(declaredType);
        var memberCount = reader.ReadInt32();
        if (memberCount != contract.Properties.Count)
        {
            throw new InvalidDataException($"Expected {contract.Properties.Count} members for {declaredType.FullName}, but received {memberCount}.");
        }

        var values = new object?[memberCount];
        for (int index = 0; index < memberCount; index++)
        {
            values[index] = ReadValue(reader, contract.Properties[index].PropertyType);
        }

        return contract.Create(values);
    }

    private static void WriteUtf8(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumValueBytes)
        {
            throw new InvalidDataException($"A serialized value exceeds the {MaximumValueBytes}-byte limit.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadUtf8(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaximumValueBytes)
        {
            throw new InvalidDataException($"Invalid UTF-8 value length {length}.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("The length-prefixed payload ended before a complete value was read.");
        }

        return StrictUtf8.GetString(bytes);
    }
}