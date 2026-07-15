using System.Globalization;

namespace TripleG3.P2P.Serialization;

internal static class PrimitiveValueConverter
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public static bool IsSupported(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return effectiveType.IsPrimitive
            || effectiveType.IsEnum
            || effectiveType == typeof(string)
            || effectiveType == typeof(decimal)
            || effectiveType == typeof(Guid)
            || effectiveType == typeof(DateTime)
            || effectiveType == typeof(DateTimeOffset)
            || effectiveType == typeof(DateOnly)
            || effectiveType == typeof(TimeOnly)
            || effectiveType == typeof(TimeSpan);
    }

    public static string Format(object value, Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(DateTime)) return ((DateTime)value).ToString("O", InvariantCulture);
        if (type == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString("O", InvariantCulture);
        if (type == typeof(DateOnly)) return ((DateOnly)value).ToString("O", InvariantCulture);
        if (type == typeof(TimeOnly)) return ((TimeOnly)value).ToString("O", InvariantCulture);
        if (type == typeof(TimeSpan)) return ((TimeSpan)value).ToString("c", InvariantCulture);
        if (type == typeof(Guid)) return ((Guid)value).ToString("D");
        if (type == typeof(float)) return ((float)value).ToString("R", InvariantCulture);
        if (type == typeof(double)) return ((double)value).ToString("R", InvariantCulture);
        if (value is IFormattable formattable) return formattable.ToString(null, InvariantCulture);
        return value.ToString() ?? string.Empty;
    }

    public static object? Parse(string value, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;
        if (effectiveType == typeof(string)) return value;
        if (value.Length == 0) return DefaultValue(targetType);
        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, value, true);
        if (effectiveType == typeof(Guid)) return Guid.ParseExact(value, "D");
        if (effectiveType == typeof(DateTime)) return DateTime.ParseExact(value, "O", InvariantCulture, DateTimeStyles.RoundtripKind);
        if (effectiveType == typeof(DateTimeOffset)) return DateTimeOffset.ParseExact(value, "O", InvariantCulture, DateTimeStyles.RoundtripKind);
        if (effectiveType == typeof(DateOnly)) return DateOnly.ParseExact(value, "O", InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.ParseExact(value, "O", InvariantCulture);
        if (effectiveType == typeof(TimeSpan)) return TimeSpan.ParseExact(value, "c", InvariantCulture);
        return Convert.ChangeType(value, effectiveType, InvariantCulture);
    }

    public static object? DefaultValue(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null
            ? null
            : Activator.CreateInstance(type);
}