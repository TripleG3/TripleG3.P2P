using System.Threading;

namespace TripleG3.P2P.Video.Internal
{
    internal sealed class SequenceNumberGenerator
    {
        private int _value;
        public SequenceNumberGenerator(int seed = 0) => _value = seed & 0xFFFF;
        public ushort Next() => (ushort)Interlocked.Increment(ref _value);
    }
}
