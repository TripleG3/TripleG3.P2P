namespace TripleG3.P2P.Video.Internal
{
    internal sealed class SequenceNumberGenerator(int seed = 0)
    {
        private int _value = seed & 0xFFFF;

        public ushort Next() => (ushort)Interlocked.Increment(ref _value);
    }
}
