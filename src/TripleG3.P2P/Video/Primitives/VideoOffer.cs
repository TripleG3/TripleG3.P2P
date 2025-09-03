using System.Text.Json;

namespace TripleG3.P2P.Video.Primitives
{
    public sealed class VideoOffer
    {
        public CodecKind Codec { get; set; }
        public uint Ssrc { get; set; }
        public int PayloadType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? ProfileLevelId { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this);
        public static VideoOffer FromJson(string json) => JsonSerializer.Deserialize<VideoOffer>(json) ?? throw new InvalidOperationException("Invalid VideoOffer JSON");
    }
}
