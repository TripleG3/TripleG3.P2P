namespace TripleG3.P2P.Video.Rtp;

/// <summary>Very small reordering buffer keyed by sequence number with timestamp grouping.</summary>
internal sealed class RtpJitterBuffer(int capacity = 64)
{
    private readonly SortedDictionary<ushort, (uint ts, byte[] pkt)> _buffer = new();

    public void Add(ushort seq, uint ts, byte[] raw)
    {
        _buffer[seq] = (ts, raw);
        if (_buffer.Count > capacity)
        {
            // drop oldest (smallest sequence considering wrap)
            var firstKey = _buffer.Keys.First();
            _buffer.Remove(firstKey);
        }
    }

    public IEnumerable<(uint ts, byte[] pkt)> DrainInOrder()
    {
        foreach (var kvp in _buffer)
            yield return kvp.Value;
        _buffer.Clear();
    }
}
