namespace TripleG3.P2P.Video.Rtp;

/// <summary>Simple in-order release buffer for small reordering windows.</summary>
internal sealed class RtpReorderBuffer
{
    private readonly SortedDictionary<ushort, byte[]> _store = new();
    private readonly int _capacity;
    private ushort? _expected;
    public RtpReorderBuffer(int capacity) => _capacity = capacity;

    public void Add(ushort seq, byte[] packet)
    {
        _store[seq] = packet;
        if (_store.Count > _capacity)
        {
            var first = _store.First();
            if (_expected.HasValue && first.Key != _expected.Value)
                _store.Remove(first.Key); // drop oldest unrelated
        }
        _expected ??= seq; // initialize expected to first seen
    }

    public IEnumerable<byte[]> PopReady()
    {
        var list = new List<byte[]>();
        while (_expected.HasValue && _store.TryGetValue(_expected.Value, out var pkt))
        {
            list.Add(pkt);
            _store.Remove(_expected.Value);
            _expected = (ushort)(_expected.Value + 1);
        }
        return list;
    }
}
