using System.Net;

namespace TripleG3.P2P.Core;

internal sealed class OutboundEndpointSet
{
    private readonly object _gate = new();
    private IPEndPoint[] _endpoints = [];

    public IReadOnlyCollection<IPEndPoint> Endpoints
        => GetSnapshot().Select(Clone).ToArray();

    public IPEndPoint[] GetSnapshot() => Volatile.Read(ref _endpoints);

    public void Replace(IEnumerable<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var uniqueEndpoints = new List<IPEndPoint>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            Validate(endpoint, nameof(endpoints));
            var copy = Clone(endpoint);
            if (keys.Add(GetKey(copy))) uniqueEndpoints.Add(copy);
        }

        lock (_gate)
        {
            Interlocked.Exchange(ref _endpoints, uniqueEndpoints.ToArray());
        }
    }

    public bool Add(IPEndPoint endpoint)
    {
        Validate(endpoint, nameof(endpoint));
        var copy = Clone(endpoint);
        var key = GetKey(copy);

        lock (_gate)
        {
            var current = GetSnapshot();
            if (current.Any(candidate => string.Equals(GetKey(candidate), key, StringComparison.Ordinal))) return false;

            Interlocked.Exchange(ref _endpoints, [.. current, copy]);
            return true;
        }
    }

    public bool Remove(IPEndPoint endpoint)
    {
        Validate(endpoint, nameof(endpoint));
        var key = GetKey(endpoint);

        lock (_gate)
        {
            var current = GetSnapshot();
            if (!current.Any(candidate => string.Equals(GetKey(candidate), key, StringComparison.Ordinal))) return false;

            Interlocked.Exchange(
                ref _endpoints,
                current.Where(candidate => !string.Equals(GetKey(candidate), key, StringComparison.Ordinal)).ToArray());
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _endpoints, []);
        }
    }

    public static void Validate(IPEndPoint endpoint, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(endpoint, parameterName);
        if (endpoint.Port == 0)
        {
            throw new ArgumentException("Outbound endpoints must not use port zero.", parameterName);
        }
    }

    public static string GetKey(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.ToString();
    }

    private static IPEndPoint Clone(IPEndPoint endpoint) => new(endpoint.Address, endpoint.Port);
}