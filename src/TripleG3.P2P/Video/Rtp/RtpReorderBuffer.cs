using System.Diagnostics;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Bounded circular sequence-number reorder buffer that advances at frame boundaries.</summary>
internal sealed class RtpReorderBuffer
{
    private readonly int _capacity;
    private readonly TimeSpan _maximumDelay;
    private readonly Dictionary<ushort, (byte[] Packet, bool Marker, long Arrival)> _store = [];
    private ushort? _expected;

    public RtpReorderBuffer(int capacity, TimeSpan? maximumDelay = null)
    {
        if (capacity <= 0 || capacity >= short.MaxValue) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _maximumDelay = maximumDelay ?? TimeSpan.FromMilliseconds(250);
        if (_maximumDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDelay));
    }

    public bool Add(ushort sequenceNumber, byte[] packet, bool marker)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _expected ??= sequenceNumber;
        var distance = ForwardDistance(_expected.Value, sequenceNumber);
        if (distance >= short.MaxValue || _store.ContainsKey(sequenceNumber)) return false;
        _store.Add(sequenceNumber, (packet, marker, Stopwatch.GetTimestamp()));
        return true;
    }

    public IReadOnlyList<byte[]> PopReady(out int skippedPackets)
    {
        skippedPackets = 0;
        var ready = new List<byte[]>();
        PopContiguous(ready);
        if (_store.Count == 0 || !_expected.HasValue) return ready;

        var oldestArrival = _store.Values.Min(item => item.Arrival);
        var shouldAdvance = _store.Count >= _capacity
            || _store.Values.Any(item => item.Marker)
            || Stopwatch.GetElapsedTime(oldestArrival) >= _maximumDelay;
        if (!shouldAdvance) return ready;

        var next = _store.Keys.MinBy(sequenceNumber => ForwardDistance(_expected.Value, sequenceNumber));
        skippedPackets = ForwardDistance(_expected.Value, next);
        _expected = next;
        PopContiguous(ready);
        return ready;
    }

    public void Clear()
    {
        _store.Clear();
        _expected = null;
    }

    private void PopContiguous(List<byte[]> ready)
    {
        while (_expected.HasValue && _store.Remove(_expected.Value, out var entry))
        {
            ready.Add(entry.Packet);
            _expected = unchecked((ushort)(_expected.Value + 1));
        }
    }

    private static int ForwardDistance(ushort from, ushort to) => unchecked((ushort)(to - from));
}