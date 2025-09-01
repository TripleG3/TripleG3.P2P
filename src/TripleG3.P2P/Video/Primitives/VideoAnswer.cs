using System;
using System.Text.Json;

namespace TripleG3.P2P.Video.Primitives
{
    public sealed class VideoAnswer
    {
        public bool Accepted { get; set; }
        public CodecKind Codec { get; set; }
        public uint Ssrc { get; set; }
        public int PayloadType { get; set; }
        public string? ProfileLevelId { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this);
        public static VideoAnswer FromJson(string json) => JsonSerializer.Deserialize<VideoAnswer>(json) ?? throw new InvalidOperationException("Invalid VideoAnswer JSON");
    }
}
