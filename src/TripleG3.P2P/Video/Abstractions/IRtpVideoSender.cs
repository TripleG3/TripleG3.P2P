using System;
using System.Threading;
using System.Threading.Tasks;
using TripleG3.P2P.Video;
// ...existing code...

namespace TripleG3.P2P.Video.Abstractions
{
    /// <summary>
    /// High-level RTP video sender.
    /// </summary>
    public interface IRtpVideoSender : IDisposable
    {
        /// <summary>
        /// Send an encoded access unit (Annex B) as RTP packets.
        /// Returns false when the AU was dropped (backpressure/network error).
        /// </summary>
    Task<bool> SendAsync(TripleG3.P2P.Video.EncodedAccessUnit au, CancellationToken ct = default);

        /// <summary>
        /// Periodically raised with sender-side metrics.
        /// </summary>
    event Action<TripleG3.P2P.Video.Primitives.RtpVideoSenderStats>? StatsAvailable;
    }
}
