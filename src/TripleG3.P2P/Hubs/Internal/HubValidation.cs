namespace TripleG3.P2P.Hubs.Internal;

internal static class HubValidation
{
    public static void ValidateOptions(HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumMembers <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumMembers));
        if (options.MaximumUsernameLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumUsernameLength));
        if (options.MaximumMessageLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumMessageLength));
        if (options.MaximumTeamNameLength <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumTeamNameLength));
        if (options.MessageHistoryCapacity < 0) throw new ArgumentOutOfRangeException(nameof(options.MessageHistoryCapacity));
        if (options.NotificationHistoryCapacity < 0) throw new ArgumentOutOfRangeException(nameof(options.NotificationHistoryCapacity));
    }

    public static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty identifier is required.", parameterName);
    }

    public static string NormalizeName(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength) throw new ArgumentOutOfRangeException(parameterName);
        return normalized;
    }

    public static string NormalizeMessage(string value, int maximumLength)
        => NormalizeName(value, maximumLength, nameof(value));

    public static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    public static IReadOnlyList<T> AppendBounded<T>(IReadOnlyList<T> source, T value, int capacity)
    {
        if (capacity == 0) return Array.AsReadOnly(Array.Empty<T>());
        var skip = Math.Max(0, source.Count - capacity + 1);
        return Array.AsReadOnly(source.Skip(skip).Append(value).ToArray());
    }
}